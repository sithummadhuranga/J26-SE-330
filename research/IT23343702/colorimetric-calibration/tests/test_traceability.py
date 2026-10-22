"""Requirements traceability: every proposal requirement (FR1-12, NFR1-9) and the two added after the first presentation, mapped to the
tests that cover it and an honest status. scripts/trace_requirements.py prints the table; these tests keep the mapping truthful."""

import importlib.util
import json
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("trace", ROOT / "scripts" / "trace_requirements.py")
trace = importlib.util.module_from_spec(spec)
spec.loader.exec_module(trace)
DOC = json.loads((ROOT / "requirements.json").read_text())
REQS = DOC["requirements"]


def test_every_proposal_requirement_is_listed_once():
    ids = [r["id"] for r in REQS]
    assert len(ids) == len(set(ids))
    assert {f"FR{i}" for i in range(1, 13)} | {f"NFR{i}" for i in range(1, 10)} <= set(ids)


def test_the_requirements_added_after_pp1_are_marked_as_such():
    added = {r["id"] for r in REQS if r.get("added_after_pp1")}
    assert added == {"FR13", "NFR10"}


def test_statuses_are_from_the_fixed_list_and_every_row_explains_itself():
    for r in REQS:
        assert r["status"] in trace.STATUSES, r["id"]
        assert r["text"].strip() and r["note"].strip(), r["id"]


def test_covered_needs_tests_and_open_needs_a_reason_it_is_open():
    for r in REQS:
        if r["status"] == "covered":
            assert r["tests"], f"{r['id']} is marked covered without a test"
        if r["status"] == "open":
            assert len(r["note"]) > 40, f"{r['id']} is open: say why"


def test_every_test_named_in_the_mapping_exists():
    missing = trace.missing_tests(REQS, trace.collected_tests())
    assert missing == {}, missing


def test_the_check_notices_a_test_that_does_not_exist():
    fake = [{"id": "X", "tests": ["test_api.py::test_that_is_not_there", "test_api.py::test_bad_arguments_raise"]}]
    assert trace.missing_tests(fake, trace.collected_tests()) == {"X": ["test_api.py::test_that_is_not_there"]}


def test_a_whole_file_can_be_named_and_a_missing_file_is_noticed():
    ok = [{"id": "A", "tests": ["test_api.py"]}]
    bad = [{"id": "B", "tests": ["test_nope.py"]}]
    assert trace.missing_tests(ok, trace.collected_tests()) == {}
    assert trace.missing_tests(bad, trace.collected_tests()) == {"B": ["test_nope.py"]}


def test_the_table_has_one_row_per_requirement_and_counts_by_status():
    table, counts = trace.render(REQS, trace.collected_tests())
    assert len(table.splitlines()) >= len(REQS) + 2
    assert sum(counts.values()) == len(REQS)


def test_main_exits_zero_for_the_real_mapping_and_one_for_a_broken_one(tmp_path):
    assert trace.main([]) == 0
    broken = tmp_path / "r.json"
    broken.write_text(json.dumps({"requirements": [{"id": "FR1", "text": "t", "status": "covered", "note": "n", "tests": ["test_api.py::nope"]}]}))
    assert trace.main(["--file", str(broken)]) == 1
