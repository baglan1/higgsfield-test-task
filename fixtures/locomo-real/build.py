#!/usr/bin/env python3
"""
Convert a slice of the real LoCoMo dataset into our fixture format.

Strategy for staying under 5 minutes wall time:
- pick ONE conversation from locomo10.json
- take the first 2 sessions (~35 LoCoMo messages)
- batch consecutive messages 2-at-a-time into our /turns shape (~17 /turns calls)
- pick ~10 QA pairs whose evidence dia_ids are all in the included sessions
- balance across the 5 LoCoMo categories

Run from the repo root:  python3 fixtures/locomo-real/build.py [path/to/locomo10.json]
"""

import json
import re
import sys
import datetime
from pathlib import Path

LOCOMO_JSON = sys.argv[1] if len(sys.argv) > 1 else "/tmp/locomo/data/locomo10.json"
SAMPLE_INDEX = 0
# 6 sessions ≈ 108 LoCoMo messages → ~54 /turns calls. Combined with ~25 probes,
# this lands the eval near the 5-minute wall-time budget against gpt-4o-mini.
INCLUDED_SESSIONS = [f"session_{i}" for i in range(1, 7)]
QA_PER_CATEGORY = 5
MAX_PROBES = 25
# LoCoMo conversations have two human speakers. Our extractor is anchored on "the USER",
# so we pick ONE as the focal speaker (their messages are role=user) and treat the other
# as role=assistant. QA pairs that are about the non-focal speaker are dropped.
FOCAL_SPEAKER = "Caroline"

CATEGORY_NAMES = {
    1: "multi-hop",
    2: "temporal",
    3: "open-domain",
    4: "single-hop",
    5: "adversarial",
}

OUT = Path(__file__).parent
CONVERSATIONS = OUT / "conversations"
PROBES = OUT / "probes"

# wipe previous output
for p in CONVERSATIONS.glob("*.json"):
    p.unlink()
for p in PROBES.glob("*.json"):
    p.unlink()


def parse_dt(s: str) -> datetime.datetime:
    """LoCoMo uses '1:56 pm on 8 May, 2023'."""
    return datetime.datetime.strptime(s, "%I:%M %p on %d %B, %Y")


def evidence_in_sessions(ev, allowed_session_nums):
    if not ev:
        return False
    for e in ev:
        m = re.match(r"^D(\d+):\d+", e)
        if not m or int(m.group(1)) not in allowed_session_nums:
            return False
    return True


def extract_key_substring(answer: str) -> list[str]:
    """Pull a distinctive token from a free-form answer for substring matching."""
    a = answer.lower()
    # Year — most distinctive when present.
    year = re.search(r"\b(19|20)\d{2}\b", a)
    if year:
        return [year.group()]
    # Multi-word noun phrase: take the first non-stopword token of length >= 4.
    stop = {"the", "and", "for", "are", "was", "with", "her", "his", "they", "she",
            "you", "have", "this", "that", "from", "what", "when", "will", "would"}
    tokens = re.findall(r"[a-z][a-z']{3,}", a)
    primary = next((t for t in tokens if t not in stop), None)
    if primary:
        return [primary]
    # Numeric without year.
    num = re.search(r"\d+", a)
    if num:
        return [num.group()]
    return [a.strip()[:20]]


def main() -> None:
    with open(LOCOMO_JSON) as f:
        data = json.load(f)

    sample = data[SAMPLE_INDEX]
    sample_id = sample["sample_id"]
    user_id = f"u-locomo-{sample_id}"
    allowed_session_nums = {int(s.split("_")[1]) for s in INCLUDED_SESSIONS}

    # ---- Build the conversation in our /turns shape ----
    turns_out = []
    for session_key in INCLUDED_SESSIONS:
        msgs = sample["conversation"][session_key]
        base_dt = parse_dt(sample["conversation"][f"{session_key}_date_time"])
        # Pair consecutive messages so each /turns call has ~2 messages.
        i = 0
        minute_offset = 0
        while i < len(msgs):
            batch = msgs[i:i + 2]
            i += 2
            ts = (base_dt + datetime.timedelta(minutes=minute_offset)).isoformat() + "Z"
            minute_offset += 2
            messages = [
                {
                    "role": "user" if m["speaker"] == FOCAL_SPEAKER else "assistant",
                    "name": m["speaker"],
                    "content": m["text"],
                }
                for m in batch
            ]
            turns_out.append({
                "session_id": session_key,
                "timestamp": ts,
                "messages": messages,
            })

    conv_obj = {"id": sample_id, "user_id": user_id, "turns": turns_out}
    conv_path = CONVERSATIONS / f"01_{sample_id}_s1s2.json"
    with open(conv_path, "w") as f:
        json.dump(conv_obj, f, indent=2)
    print(f"wrote {conv_path}: {len(turns_out)} turns from {len(INCLUDED_SESSIONS)} sessions")

    # ---- Filter and pick QA ----
    # Only include QAs whose subject is the focal speaker — drop ones that explicitly ask
    # about the other speaker (their facts aren't in the focal speaker's memory store).
    other_speakers = {
        m["speaker"]
        for s in INCLUDED_SESSIONS
        for m in sample["conversation"][s]
        if m["speaker"] != FOCAL_SPEAKER
    }

    def is_about_focal(q: dict) -> bool:
        # If question mentions the focal speaker, keep it.
        if FOCAL_SPEAKER.lower() in q["question"].lower():
            return True
        # If question mentions another speaker by name, drop it.
        if any(other.lower() in q["question"].lower() for other in other_speakers):
            return False
        # Otherwise, ambiguous — keep it (event-level questions like "What did the charity race…").
        return True

    eligible = [
        q for q in sample["qa"]
        if evidence_in_sessions(q.get("evidence", []), allowed_session_nums)
        and is_about_focal(q)
    ]

    selected = []
    for cat in [4, 1, 2, 3, 5]:  # single-hop and multi-hop first as they're highest-value
        cat_qa = [q for q in eligible if q.get("category") == cat]
        for q in cat_qa[:QA_PER_CATEGORY]:
            selected.append(q)
            if len(selected) >= MAX_PROBES:
                break
        if len(selected) >= MAX_PROBES:
            break

    # ---- Build probes ----
    for idx, qa in enumerate(selected, 1):
        cat = qa["category"]
        cat_name = CATEGORY_NAMES[cat]

        if cat == 5:
            # Adversarial: question whose true answer is "no info"; the adversarial_answer
            # is the misleading one we should NOT surface.
            adv = str(qa.get("adversarial_answer", "")).lower()
            absent = extract_key_substring(adv) if adv else []
            expect = []
        else:
            ans = str(qa.get("answer", ""))
            expect = extract_key_substring(ans)
            absent = []

        probe = {
            "id": f"q{idx:02d}_cat{cat}",
            "category": cat_name,
            "user_id": user_id,
            "session_id": "s-probe",
            "query": qa["question"],
            "max_tokens": 512,
            "expect_substrings": expect,
            "expect_absent_substrings": absent,
            "_locomo_evidence": qa["evidence"],
            "_locomo_answer": str(qa.get("answer", qa.get("adversarial_answer", ""))),
        }
        probe_path = PROBES / f"{idx:02d}_cat{cat}.json"
        with open(probe_path, "w") as f:
            json.dump(probe, f, indent=2)
        print(f"wrote {probe_path}: cat={cat} expect={expect} absent={absent}")
        print(f"    Q: {qa['question']}")
        print(f"    A: {probe['_locomo_answer']}")


if __name__ == "__main__":
    main()
