#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Commit a Live Photo as one Google Photos item.

The protobuf layout follows the same create-media fields already used by the iOS client
(harbor_live.py): field 24 links a video to a new still, field 9 attaches a video to an
existing still. Schema reference: xob0t/gotohp create_media_items.go (MIT, copyright 2024 xob0t).
"""
import base64
import hashlib
import json
import os
import sys
from pathlib import Path

from blackboxprotobuf import decode_message
from gpmc.message_types import COMMIT_UPLOAD_RESPONSE, FIND_REMOTE_MEDIA_BY_HASH_RESPONSE

RESULT_MASK = base64.b64decode("CnsKAggBEAEaAggBIgIIASoQCgIIARICCAEaAggBIgIIATICCAGCAQIIAYoBAggBmgECCAGiAQIIAaoBCggBKgIgATICCAHKAQIIAfIBBggBEgIIAfoBAggBggICCAGKAgQKAggBkgIGCAESAggBogICCAGqAgC6AgDCAgAQASpiCAESKggBEhIIARoGCAESAggBIgQIASIAKAEiBggBEgIQASoGCAESAggBMAE4ARoWCAESDAgBGgIIASICCAEoARoCCAEwASIKCAESBggBEgIIASoOCgQIATABEgYIARICCAE6AggBQgIIAUoKCAESAggBGgISAFoSEgIIARoCCAEiCAgBEgQIARACYgIIAXISEgIIARoCCAEiCAgBEgQIARACeggKAggBIgIIAYoBBAoCCAGSAQQIARABqgECCgA=")


def varint(value):
    value &= (1 << 64) - 1
    result = bytearray()
    while value > 127:
        result.append((value & 127) | 128)
        value >>= 7
    result.append(value)
    return bytes(result)


def integer(field, value):
    return varint(field << 3) + varint(value)


def blob(field, value):
    return varint((field << 3) | 2) + varint(len(value)) + value


def emit(event):
    print(json.dumps(event, ensure_ascii=False, separators=(",", ":")), file=sys.stderr, flush=True)


def result(payload):
    print("__GPMC_RESULT__:" + json.dumps(payload, ensure_ascii=False, separators=(",", ":")), flush=True)


def sha1_file(path):
    digest = hashlib.sha1()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.digest()


def upload_raw(api, path, digest):
    token = api.get_upload_token(base64.b64encode(digest).decode("ascii"), os.path.getsize(path))
    headers = {
        "Accept-Encoding": "gzip",
        "Accept-Language": api.language,
        "User-Agent": api.user_agent,
        "Authorization": "Bearer " + api.bearer_token,
    }
    with api._new_session() as session, open(path, "rb") as handle:
        response = session.put(
            "https://photos.googleapis.com/data/upload/uploadmedia/interactive?upload_id=" + token,
            headers=headers,
            data=handle,
            timeout=api.timeout,
        )
    response.raise_for_status()
    decoded, _ = decode_message(response.content, {"1": {"type": "int"}, "2": {"type": "bytes"}})
    if decoded.get("1") != 2 or not isinstance(decoded.get("2"), (bytes, bytearray)) or not decoded["2"]:
        raise RuntimeError("UPLOAD_RESPONSE_INVALID")
    return response.content


def post(api, method, body, schema):
    headers = {
        "Authorization": "Bearer " + api.bearer_token,
        "User-Agent": api.user_agent,
        "Content-Type": "application/x-protobuf",
        "Accept-Language": api.language,
        "Accept-Encoding": "gzip",
        "x-goog-ext-173412678-bin": "CgcIAhClARgC",
        "x-goog-ext-174067345-bin": "CgIIAg==",
    }
    with api._new_session() as session:
        response = session.post(
            "https://photosdata-pa.googleapis.com/6439526531001121323/" + method,
            data=body,
            headers=headers,
            timeout=api.timeout,
        )
    if response.status_code == 400:
        raise RuntimeError("PAIR_COMMIT_REJECTED")
    response.raise_for_status()
    return decode_message(response.content, schema)[0]


def motion_key(api, digest):
    query = blob(1, digest) + integer(5, 2)
    response = post(api, "5084965799730810217", blob(1, blob(1, query) + blob(2, b"")), FIND_REMOTE_MEDIA_BY_HASH_RESPONSE)
    from gpmc.db_update_parser import _get_nested
    key = _get_nested(response, "1", "2", "2", "1")
    return key if isinstance(key, str) and key else ""


def commit(api, route, quality, name, created, photo_hash, video_hash, photo_receipt, video_receipt):
    policy = 1 if quality == "saver" else 3
    model = {"original": "Pixel XL", "quota": "Pixel 8", "saver": "Pixel 2"}[quality]
    primary = photo_receipt if route == "joint" else video_receipt
    primary_hash = photo_hash if route == "joint" else video_hash
    blueprint = blob(1, primary) + blob(2, name.encode()) + blob(3, primary_hash)
    blueprint += blob(5, integer(1, created)) + blob(6, integer(1, created)) + integer(7, policy) + integer(10, 1)
    if route == "joint":
        blueprint += blob(24, blob(1, video_receipt) + blob(2, video_hash))
    else:
        blueprint += blob(9, integer(2, 1) + blob(3, photo_hash))
    device = blob(3, model.encode()) + blob(4, b"Google") + integer(5, api.android_api_version)
    response = post(api, "16538846908252377752", blob(1, blueprint) + blob(2, device) + blob(5, RESULT_MASK), COMMIT_UPLOAD_RESPONSE)
    from gpmc.db_update_parser import _get_nested
    key = _get_nested(response, "1", "3", "1")
    if not isinstance(key, str) or not key:
        raise RuntimeError("COMMIT_UNCONFIRMED")
    return key


def safe_name(path, fallback):
    name = fallback or os.path.basename(path)
    if not name or "/" in name or "\\" in name or len(name.encode()) > 2048:
        raise RuntimeError("INVALID_PAIR_NAME")
    return name


def one_pair(api, item, quality, route):
    photo = item["photo"]
    video = item["video"]
    photo_name = safe_name(photo, item.get("photo_name"))
    video_name = safe_name(video, item.get("video_name"))
    created = int(item.get("created", 0) or 0)
    emit({"phase": "hashing", "filename": photo_name, "path": photo, "bytes_completed": 0, "bytes_total": os.path.getsize(photo)})
    photo_hash = sha1_file(photo)
    video_hash = sha1_file(video)
    still_key = api.find_remote_media_by_hash(photo_hash) or ""
    movie_key = motion_key(api, video_hash)
    if still_key and still_key == movie_key:
        emit({"phase": "skipped", "filename": photo_name, "path": photo, "bytes_completed": os.path.getsize(photo), "bytes_total": os.path.getsize(photo)})
        emit({"phase": "skipped", "filename": video_name, "path": video, "bytes_completed": os.path.getsize(video), "bytes_total": os.path.getsize(video)})
        return still_key, "skipped"
    chosen = "joint" if route == "joint" and not still_key else "reconcile"
    if chosen == "reconcile" and not still_key:
        emit({"phase": "uploading", "filename": photo_name, "path": photo, "bytes_completed": 0, "bytes_total": os.path.getsize(photo)})
        receipt = upload_raw(api, photo, photo_hash)
        decode_message(receipt)
        still_key = api.commit_upload(decode_message(receipt)[0], photo_name, photo_hash, "saver" if quality == "saver" else "original", model={"original": "Pixel XL", "quota": "Pixel 8", "saver": "Pixel 2"}[quality])
    emit({"phase": "uploading", "filename": video_name, "path": video, "bytes_completed": 0, "bytes_total": os.path.getsize(video)})
    video_receipt = upload_raw(api, video, video_hash)
    photo_receipt = upload_raw(api, photo, photo_hash) if chosen == "joint" else video_receipt
    key = commit(api, chosen, quality, photo_name if chosen == "joint" else video_name, created, photo_hash, video_hash, photo_receipt, video_receipt)
    phase_photo = os.path.getsize(photo)
    phase_video = os.path.getsize(video)
    emit({"phase": "completed", "filename": photo_name, "path": photo, "bytes_completed": phase_photo, "bytes_total": phase_photo})
    emit({"phase": "completed", "filename": video_name, "path": video, "bytes_completed": phase_video, "bytes_total": phase_video})
    return key, "done"


def main():
    config = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
    script_dir = os.path.dirname(os.path.abspath(__file__))
    if script_dir not in sys.path:
        sys.path.insert(0, script_dir)
    custom = config.get("gpmc_path") or ""
    if custom and os.path.isdir(custom) and custom not in sys.path:
        sys.path.append(custom)
    from gpmc import Client

    client = Client(
        auth_data=config["auth_data"],
        proxy=config.get("proxy") or "",
        timeout=int(config.get("timeout") or 60),
        log_level="WARNING",
    )
    quality = config.get("quality") or "original"
    route = config.get("route") or "joint"
    if quality not in ("original", "quota", "saver") or route not in ("joint", "reconcile"):
        result({"status": "error", "error": "INVALID_PAIR_POLICY"})
        return 1
    results = {}
    failed = []
    for item in config.get("pairs") or []:
        photo = item.get("photo")
        video = item.get("video")
        try:
            key, _state = one_pair(client.api, item, quality, route)
            results[os.path.abspath(photo)] = key
            results[os.path.abspath(video)] = key
        except Exception as error:
            failed.append({"photo": photo, "video": video, "error": str(error)})
            emit({"phase": "failed", "filename": os.path.basename(photo or ""), "path": photo or "", "error": str(error), "bytes_completed": 0, "bytes_total": 0})
    result({"status": "ok", "results": results, "failed": failed})
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        result({"status": "error", "error": str(error)})
        sys.exit(1)
