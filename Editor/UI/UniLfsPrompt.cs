using System;
using UnityEditor;
using UnityEngine;

namespace UniLFS.Editor
{
    /// <summary>
    /// The questions UniLFS asks on its own initiative — Auto Pull, Auto Push
    /// and the setup reminder — asked without taking the editor hostage.
    ///
    /// All three decide to ask during editor startup, from
    /// <c>[InitializeOnLoadMethod]</c> then <c>delayCall</c>. Asked with
    /// <c>EditorUtility.DisplayDialog</c> that is a modal dialog, and a modal
    /// dialog stops the main thread until somebody clicks a button — and nobody
    /// promised there is somebody. A GUI editor driven by an automation harness
    /// (a Unity MCP server, a CLI-driven editor loop, a remote runner) is not
    /// batch mode, so the <c>Application.isBatchMode</c> guard those prompts
    /// carry does not cover it: the editor hung on the first launch after a
    /// teammate pushed, until a human closed the dialog by hand.
    ///
    /// The same questions are asked here from a floating utility window, which
    /// never blocks the main thread. Closing the window counts as declining, so
    /// the Console records the outcome either way and "nobody answered" is no
    /// longer indistinguishable from "answered Later" in the log.
    /// </summary>
    static class UniLfsPrompt
    {
        /// <summary>
        /// Set to anything other than <c>0</c> to stop UniLFS opening prompts
        /// at all: a mode that would ask writes the Console line its <c>off</c>
        /// setting writes instead, and nothing waits for an answer.
        ///
        /// The escape hatch is an environment variable because Auto Pull and
        /// Auto Push live in committed project settings — one machine running
        /// an unattended editor cannot turn them off for itself without turning
        /// them off for the whole team.
        /// </summary>
        public const string EnvNoPrompts = "UNILFS_NO_PROMPTS";

        /// <summary>
        /// Whether prompting is off for this editor. Batch mode has never
        /// prompted; <see cref="EnvNoPrompts"/> extends the same treatment to a
        /// GUI editor nobody is watching.
        /// </summary>
        internal static bool Suppressed
        {
            get { return Application.isBatchMode || SuppressedByEnvironment; }
        }

        /// <summary>Split out so tests can read the variable without a batch-mode editor.</summary>
        internal static bool SuppressedByEnvironment
        {
            get
            {
                string value = Environment.GetEnvironmentVariable(EnvNoPrompts);
                return !string.IsNullOrEmpty(value) && value != "0";
            }
        }

        /// <summary>
        /// True while one of these prompts is on screen. A caller that would
        /// raise a second one waits instead: the open window still owns that
        /// decision, and two windows asking about the same files is worse than
        /// one asking late.
        /// </summary>
        internal static bool IsOpen
        {
            get
            {
                var windows = Resources.FindObjectsOfTypeAll<UniLfsPromptWindow>();
                foreach (var window in windows)
                    if (window != null) return true;
                return false;
            }
        }

        /// <summary>
        /// Opens the prompt and returns immediately. <paramref name="answered"/>
        /// runs on a later editor tick with the index of the button pressed — 0,
        /// 1 or 2, the way <c>EditorUtility.DisplayDialogComplex</c> numbers
        /// them — and with 1 when the window is closed unanswered. Pass null for
        /// <paramref name="alternative"/> for a two-button prompt.
        /// </summary>
        internal static void Ask(string title, string message, string accept, string decline, string alternative, Action<int> answered)
        {
            var window = ScriptableObject.CreateInstance<UniLfsPromptWindow>();
            window.Set(title, message, accept, decline, alternative, answered);
            window.ShowUtility();
            window.Focus();
        }
    }

    /// <summary>
    /// The window behind <see cref="UniLfsPrompt"/>. Utility rather than modal:
    /// it floats above the editor while the main thread keeps ticking
    /// underneath.
    /// </summary>
    class UniLfsPromptWindow : EditorWindow
    {
        [SerializeField] string _message = "";
        [SerializeField] string _accept = "";
        [SerializeField] string _decline = "";
        [SerializeField] string _alternative = "";
        /// <summary>
        /// Whether <see cref="Set"/> ever ran on this window. It survives a
        /// domain reload and <see cref="_answered"/> does not, so the pair tells
        /// a freshly created window (no callback yet) apart from one whose
        /// callback a reload took away.
        /// </summary>
        [SerializeField] bool _bound;

        /// <summary>
        /// Not serialized, so a domain reload (any script compile) leaves it
        /// null. The window closes itself in that case rather than sitting there
        /// with buttons that do nothing — which would also leave
        /// <see cref="UniLfsPrompt.IsOpen"/> true for the rest of the session
        /// and stop the next check ever asking. Because nothing was answered,
        /// the caller recorded no outcome and that next check does ask.
        /// </summary>
        Action<int> _answered;
        bool _replied;
        Vector2 _scroll;

        void OnEnable()
        {
            // Not Close() straight away: OnEnable runs while Unity is still
            // deserializing the window.
            if (_bound && _answered == null && !_replied)
                EditorApplication.delayCall += CloseOrphan;
        }

        void CloseOrphan()
        {
            if (this == null) return;
            if (_answered != null || _replied) return;
            Close();
        }

        internal void Set(string title, string message, string accept, string decline, string alternative, Action<int> answered)
        {
            titleContent = new GUIContent(title);
            _message = message ?? "";
            _accept = accept ?? "OK";
            _decline = decline ?? "Cancel";
            _alternative = alternative ?? "";
            _answered = answered;
            _bound = true;
            minSize = new Vector2(440, 170);
        }

        void OnGUI()
        {
            if (_answered == null && !_replied)
            {
                Close();
                return;
            }

            EditorGUILayout.Space();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField(_message, EditorStyles.wordWrappedLabel);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();

            int choice = -1;
            EditorGUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(_alternative) && GUILayout.Button(_alternative, GUILayout.Height(24)))
                choice = 2;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_decline, GUILayout.Height(24), GUILayout.MinWidth(90)))
                choice = 1;
            if (GUILayout.Button(_accept, GUILayout.Height(24), GUILayout.MinWidth(120)))
                choice = 0;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();

            // Last thing the frame draws, so closing here cannot pull the rug
            // from under a layout that is still being built.
            if (choice >= 0) Reply(choice);
        }

        void OnDestroy()
        {
            // Closing the window is an answer: "Later". Anything else would let
            // a prompt disappear without a word in the Console.
            Answer(1);
        }

        void Reply(int choice)
        {
            Answer(choice);
            Close();
        }

        void Answer(int choice)
        {
            if (_replied) return;
            _replied = true;
            var answered = _answered;
            _answered = null;
            if (answered == null) return;
            // Off this frame, because these callbacks start pulls and pushes,
            // open progress bars and write settings - and the two callers are
            // the middle of OnGUI and the middle of the window being destroyed.
            //
            // The next editor tick rather than EditorApplication.delayCall: a
            // delayCall registered from OnDestroy is dropped, so closing the
            // window would have gone unanswered and unlogged, which is the
            // silence this whole prompt exists to avoid.
            EditorApplication.CallbackFunction onNextTick = null;
            onNextTick = () =>
            {
                EditorApplication.update -= onNextTick;
                answered(choice);
            };
            EditorApplication.update += onNextTick;
        }
    }
}
