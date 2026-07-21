#!/usr/bin/env python3
"""Verify that every blob referenced by unilfs.manifest.json exists in remote
storage - without Unity and without third-party packages (Python 3.8+, stdlib
only). Ideal as a fast CI gate: if someone commits a manifest but forgot to
Push the blobs, this exits non-zero.

Reads provider configuration from ProjectSettings/UniLFSSettings.json and the
same environment variables as the Unity CLI:

  S3-compatible:  UNILFS_S3_ACCESS_KEY_ID, UNILFS_S3_SECRET_ACCESS_KEY
  Google Drive:   UNILFS_DRIVE_CLIENT_ID, UNILFS_DRIVE_CLIENT_SECRET,
                  UNILFS_DRIVE_REFRESH_TOKEN

Usage:
  python3 verify_manifest.py [unity_project_root]   # default: current dir

Exit codes: 0 = all blobs present, 1 = missing blobs or errors.
"""
import concurrent.futures
import datetime
import hashlib
import hmac
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

EMPTY_SHA256 = hashlib.sha256(b"").hexdigest()


def load_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


# ---------- S3-compatible (SigV4 HEAD) ----------

def s3_head(endpoint, bucket, region, prefix, access_key, secret_key, blob_hash):
    parsed = urllib.parse.urlparse(endpoint.strip())
    host = parsed.netloc
    segments = [bucket]
    if prefix and prefix.strip("/"):
        segments += [s for s in prefix.strip("/").split("/") if s]
    segments += ["objects", blob_hash[:2], blob_hash]
    path = "/" + "/".join(urllib.parse.quote(s, safe="") for s in segments)

    now = datetime.datetime.now(datetime.timezone.utc)
    amz_date = now.strftime("%Y%m%dT%H%M%SZ")
    date_stamp = now.strftime("%Y%m%d")

    headers = [("host", host), ("x-amz-content-sha256", EMPTY_SHA256), ("x-amz-date", amz_date)]
    canonical_headers = "".join(f"{k}:{v}\n" for k, v in headers)
    signed_headers = ";".join(k for k, _ in headers)
    canonical_request = "\n".join(["HEAD", path, "", canonical_headers, signed_headers, EMPTY_SHA256])

    scope = f"{date_stamp}/{region}/s3/aws4_request"
    string_to_sign = "\n".join([
        "AWS4-HMAC-SHA256", amz_date, scope,
        hashlib.sha256(canonical_request.encode()).hexdigest(),
    ])

    def hmac_sha256(key, data):
        return hmac.new(key, data.encode(), hashlib.sha256).digest()

    k_date = hmac_sha256(("AWS4" + secret_key).encode(), date_stamp)
    k_region = hmac_sha256(k_date, region)
    k_service = hmac_sha256(k_region, "s3")
    k_signing = hmac_sha256(k_service, "aws4_request")
    signature = hmac.new(k_signing, string_to_sign.encode(), hashlib.sha256).hexdigest()

    request = urllib.request.Request(
        f"{parsed.scheme}://{host}{path}",
        method="HEAD",
        headers={
            "x-amz-date": amz_date,
            "x-amz-content-sha256": EMPTY_SHA256,
            "Authorization": (
                f"AWS4-HMAC-SHA256 Credential={access_key}/{scope}, "
                f"SignedHeaders={signed_headers}, Signature={signature}"
            ),
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status == 200
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return False
        raise RuntimeError(f"S3 HEAD failed with HTTP {e.code} (check credentials/bucket/clock)") from e


# ---------- Google Drive ----------

def drive_access_token(client_id, client_secret, refresh_token):
    body = urllib.parse.urlencode({
        "client_id": client_id,
        "client_secret": client_secret,
        "refresh_token": refresh_token,
        "grant_type": "refresh_token",
    }).encode()
    with urllib.request.urlopen("https://oauth2.googleapis.com/token", body, timeout=30) as response:
        return json.load(response)["access_token"]


def drive_exists(token, folder_id, blob_hash):
    query = urllib.parse.quote(
        f"name='{blob_hash}' and '{folder_id}' in parents and trashed=false", safe="")
    url = ("https://www.googleapis.com/drive/v3/files?q=" + query +
           "&pageSize=1&fields=files(id)&supportsAllDrives=true&includeItemsFromAllDrives=true")
    request = urllib.request.Request(url, headers={"Authorization": "Bearer " + token})
    with urllib.request.urlopen(request, timeout=30) as response:
        return bool(json.load(response).get("files"))


# ---------- main ----------

def main():
    root = sys.argv[1] if len(sys.argv) > 1 else "."
    manifest = load_json(os.path.join(root, "unilfs.manifest.json"))
    settings = load_json(os.path.join(root, "ProjectSettings", "UniLFSSettings.json"))

    paths_by_hash = {}
    for entry in manifest.get("files", []):
        paths_by_hash.setdefault(entry["hash"], []).append(entry["path"])
    if not paths_by_hash:
        print("UniLFS verify: no tracked files.")
        return 0

    provider = settings.get("provider", "s3")
    if provider == "googledrive":
        client_id = os.environ.get("UNILFS_DRIVE_CLIENT_ID") or settings.get("driveClientId", "")
        client_secret = os.environ.get("UNILFS_DRIVE_CLIENT_SECRET") or settings.get("driveClientSecret", "")
        refresh_token = os.environ.get("UNILFS_DRIVE_REFRESH_TOKEN", "")
        folder_id = settings.get("driveFolderId", "")
        if not (client_id and client_secret and refresh_token and folder_id):
            print("UniLFS verify: Google Drive credentials/folder missing "
                  "(UNILFS_DRIVE_CLIENT_ID / _SECRET / _REFRESH_TOKEN and driveFolderId).", file=sys.stderr)
            return 1
        token = drive_access_token(client_id, client_secret, refresh_token)
        check = lambda h: drive_exists(token, folder_id, h)
    else:
        access_key = os.environ.get("UNILFS_S3_ACCESS_KEY_ID", "")
        secret_key = os.environ.get("UNILFS_S3_SECRET_ACCESS_KEY", "")
        endpoint = settings.get("s3Endpoint", "")
        bucket = settings.get("s3Bucket", "")
        region = settings.get("s3Region", "auto") or "auto"
        prefix = settings.get("s3Prefix", "")
        if not (access_key and secret_key and endpoint and bucket):
            print("UniLFS verify: S3 credentials/config missing "
                  "(UNILFS_S3_ACCESS_KEY_ID / _SECRET_ACCESS_KEY and s3Endpoint/s3Bucket).", file=sys.stderr)
            return 1
        check = lambda h: s3_head(endpoint, bucket, region, prefix, access_key, secret_key, h)

    missing, errors = [], []
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        futures = {pool.submit(check, h): h for h in paths_by_hash}
        for future in concurrent.futures.as_completed(futures):
            blob_hash = futures[future]
            try:
                if not future.result():
                    missing.append(blob_hash)
            except Exception as e:  # noqa: BLE001 - report and fail
                errors.append(f"{blob_hash[:8]}...: {e}")

    total = len(paths_by_hash)
    for blob_hash in sorted(missing):
        print(f"MISSING: {', '.join(paths_by_hash[blob_hash])} ({blob_hash[:8]}...)", file=sys.stderr)
    for error in errors:
        print("ERROR: " + error, file=sys.stderr)

    if missing or errors:
        print(f"UniLFS verify: FAILED - {len(missing)} missing, {len(errors)} errors "
              f"(of {total} blobs). Did someone forget to Push before committing the manifest?", file=sys.stderr)
        return 1
    print(f"UniLFS verify: OK - all {total} blob(s) present.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
