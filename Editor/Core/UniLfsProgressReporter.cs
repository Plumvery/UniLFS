using System;
using System.Collections.Generic;
using System.IO;

namespace UniLFS.Editor
{
    /// <summary>
    /// Collects the out-of-order updates that parallel transfers produce into a
    /// single calm progress bar.
    ///
    /// Reporting straight from the workers made the gauge unreadable: each one
    /// carried its own stale snapshot of the "done" counter, each one replaced
    /// the label with its own file name, and every stage (hash / check /
    /// transfer) restarted the bar from zero. So the reporter owns the numbers
    /// instead — phases occupy fixed slices of the bar, in-flight byte counts
    /// are summed rather than overwritten, the label sticks to the
    /// longest-running item, and the reported fraction never moves backwards.
    ///
    /// Safe to call from any thread. A null inner progress turns every call into
    /// a no-op, so callers do not need null checks.
    /// </summary>
    public sealed class UniLfsProgressReporter
    {
        /// <summary>Below this much movement a report is dropped, unless something structural changed.</summary>
        const float FractionEpsilon = 0.002f;

        sealed class Running
        {
            public long Ordinal;
            public string Name;
            public long Size;
            public long Bytes;
        }

        readonly IProgress<UniLfsProgress> _outer;
        readonly object _gate = new object();
        readonly Dictionary<int, Running> _running = new Dictionary<int, Running>();

        string _phase = "";
        float _phaseStart;
        float _phaseSpan = 1f;
        int _items;
        int _itemsDone;
        long _phaseBytes;
        long _bytesDone;

        int _nextToken;
        long _nextOrdinal;
        float _fraction;
        float _sentFraction = -1f;
        string _sentLabel;

        public UniLfsProgressReporter(IProgress<UniLfsProgress> outer)
        {
            _outer = outer;
        }

        /// <summary>
        /// Starts a phase owning the [start, start + span] slice of the overall
        /// bar. Slicing is what keeps one operation's several stages on a single
        /// 0-100% run instead of three separate sweeps.
        /// </summary>
        /// <param name="items">Item count in this phase; 0 means "nothing to do", which fills the slice.</param>
        /// <param name="totalBytes">Total bytes, when known — the phase is then weighted by size instead of file count.</param>
        public void BeginPhase(string phase, float start, float span, int items, long totalBytes)
        {
            if (_outer == null) return;
            lock (_gate)
            {
                _running.Clear();
                _phase = phase;
                _phaseStart = start;
                _phaseSpan = span;
                _items = items;
                _itemsDone = 0;
                _phaseBytes = totalBytes;
                _bytesDone = 0;
            }
            Emit(null, true);
        }

        /// <summary>
        /// Registers an item as in flight. Dispose the result when it finishes;
        /// pass it straight to a provider as the byte sink.
        /// </summary>
        public UniLfsTransfer Begin(string name, long size)
        {
            if (_outer == null) return new UniLfsTransfer(null, 0);
            int token;
            lock (_gate)
            {
                token = ++_nextToken;
                _running[token] = new Running { Ordinal = ++_nextOrdinal, Name = name, Size = size };
            }
            Emit(null, true);
            return new UniLfsTransfer(this, token);
        }

        /// <summary>Fills the bar once the operation is over, whatever slice the last phase ended on.</summary>
        public void Finish()
        {
            if (_outer == null) return;
            lock (_gate)
            {
                _running.Clear();
                _itemsDone = _items;
                _fraction = 1f;
            }
            Emit(null, true);
        }

        internal void Advance(int token, long bytes)
        {
            lock (_gate)
            {
                Running r;
                if (!_running.TryGetValue(token, out r)) return;
                // A retried transfer restarts its byte count; ignoring the
                // rewind keeps the aggregate from lurching back.
                if (bytes <= r.Bytes) return;
                r.Bytes = r.Size > 0 ? Math.Min(bytes, r.Size) : bytes;
            }
            Emit(null, false);
        }

        internal void Complete(int token)
        {
            string name;
            lock (_gate)
            {
                Running r;
                if (!_running.TryGetValue(token, out r)) return;
                _running.Remove(token);
                _itemsDone++;
                _bytesDone += r.Size;
                name = r.Name;
            }
            Emit(name, true);
        }

        void Emit(string completed, bool force)
        {
            if (_outer == null) return;
            UniLfsProgress snapshot;
            lock (_gate)
            {
                long activeBytes = 0;
                Running oldest = null;
                foreach (var r in _running.Values)
                {
                    activeBytes += r.Bytes;
                    // Oldest-first: this only changes when that item completes,
                    // so the label holds still instead of cycling through every
                    // worker on every callback.
                    if (oldest == null || r.Ordinal < oldest.Ordinal) oldest = r;
                }

                float inner;
                if (_phaseBytes > 0) inner = Clamp01((_bytesDone + activeBytes) / (float)_phaseBytes);
                else if (_items > 0) inner = Clamp01(_itemsDone / (float)_items);
                else inner = 1f;

                float candidate = Clamp01(_phaseStart + _phaseSpan * inner);
                if (candidate > _fraction) _fraction = candidate;

                int active = _running.Count;
                string item = oldest != null ? oldest.Name : completed;
                string label = _phase;
                if (_items > 0) label += " (" + _itemsDone + "/" + _items + ")";
                if (!string.IsNullOrEmpty(item)) label += "  " + Path.GetFileName(item);
                if (active > 1) label += "  +" + (active - 1);

                if (!force && label == _sentLabel && Math.Abs(_fraction - _sentFraction) < FractionEpsilon) return;
                _sentFraction = _fraction;
                _sentLabel = label;

                snapshot = new UniLfsProgress
                {
                    Phase = _phase,
                    Item = item ?? "",
                    Completed = completed,
                    Done = _itemsDone,
                    Total = _items,
                    ItemProgress = oldest != null && oldest.Size > 0 ? Clamp01(oldest.Bytes / (float)oldest.Size) : 0f,
                    Fraction = _fraction,
                    Label = label,
                    DoneBytes = _bytesDone + activeBytes,
                    TotalBytes = _phaseBytes,
                    Active = active,
                };
            }
            _outer.Report(snapshot);
        }

        static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }
    }

    /// <summary>
    /// One in-flight item. Doubles as the <see cref="IProgress{T}"/> byte sink
    /// storage providers write to, so a transfer only needs
    /// <c>using (var t = reporter.Begin(path, size))</c> around it.
    /// </summary>
    public sealed class UniLfsTransfer : IProgress<long>, IDisposable
    {
        readonly UniLfsProgressReporter _owner;
        readonly int _token;
        bool _completed;

        internal UniLfsTransfer(UniLfsProgressReporter owner, int token)
        {
            _owner = owner;
            _token = token;
        }

        public void Report(long bytesTransferred)
        {
            if (_owner != null) _owner.Advance(_token, bytesTransferred);
        }

        /// <summary>A 0..1 sink, for producers that report a ratio (file hashing) rather than bytes.</summary>
        public IProgress<float> Ratio(long size)
        {
            return _owner == null ? null : new RatioSink(this, size);
        }

        public void Dispose()
        {
            if (_completed) return;
            _completed = true;
            if (_owner != null) _owner.Complete(_token);
        }

        sealed class RatioSink : IProgress<float>
        {
            readonly UniLfsTransfer _transfer;
            readonly long _size;

            public RatioSink(UniLfsTransfer transfer, long size)
            {
                _transfer = transfer;
                _size = size;
            }

            public void Report(float value)
            {
                float clamped = value < 0f ? 0f : value > 1f ? 1f : value;
                _transfer.Report((long)(clamped * _size));
            }
        }
    }
}
