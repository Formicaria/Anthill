# ============================================================
#  ANTHILL CORE v1.4.1 - Single File Python Version
#  Requirements: pip install pydantic rich requests fastapi uvicorn
#  Optional local AI: ollama pull llama3.1:8b
#
#  v1.4.1 focus:
#  - API Safety + Local Permissions
#  - Token-protected local API with /health left open
#  - Permission gates for API actions
#  - Standardized API responses and API event logging
#  - Optional CORS disabled by default
#  - Preserves v1.4 REST API backend and v1.3.2 research cleanup
# ============================================================

from __future__ import annotations

from abc import ABC, abstractmethod
from concurrent.futures import ThreadPoolExecutor, Future
from datetime import datetime, timezone
from enum import Enum
from pathlib import Path
from threading import Lock
from typing import List, Optional, Dict, Any, Tuple
import argparse
from urllib.parse import urlparse, parse_qs, unquote
from uuid import uuid4, UUID
import json
import hashlib
import os
import platform
import re
import shlex
import shutil
import sqlite3
import subprocess
import sys
import time

import requests
from pydantic import BaseModel, Field, ValidationError
from rich import print

try:
    from fastapi import FastAPI, HTTPException, Query, Depends, Header, Request
    from fastapi.middleware.cors import CORSMiddleware
    from fastapi.responses import JSONResponse
    import uvicorn
except Exception:  # FastAPI is optional unless running --api.
    FastAPI = None
    HTTPException = None
    Query = None
    Depends = None
    Header = None
    Request = None
    CORSMiddleware = None
    JSONResponse = None
    uvicorn = None


# ============================================================
#  CONFIG
# ============================================================

SCRIPT_DIR = Path(__file__).parent.resolve()

# v1.4 local API backend. Keep local-only by default.
ENABLE_API_SERVER = True
API_HOST = "127.0.0.1"
API_PORT = 8713

# v1.4.1 local API safety. Keep the API local-only and token-protected.
# Change API_AUTH_TOKEN before using the API from any external client.
ENABLE_API_AUTH = True
API_AUTH_TOKEN = os.getenv("ANTHILL_API_TOKEN", "change-me-local-token")
ENABLE_CORS = False
API_ALLOW_ORIGINS = ["http://127.0.0.1:8713", "http://localhost:8713"]
API_DEFAULT_LIMIT = 50
API_MAX_LIMIT = 200
API_PERMISSIONS = {
    "run_mission": True,
    "approve": True,
    "reject": True,
    "apply_patch": False,
    "read_status": True,
    "read_diagnostics": True,
    "read_memory": True,
    "read_events": True,
    "read_tasks": True,
    "read_messages": True,
    "read_pheromones": True,
    "read_models": True,
    "read_sources": True,
    "read_patches": True,
    "read_approvals": True,
}

OLLAMA_MODEL = "llama3.1:8b"
OLLAMA_HOST = "http://localhost:11434"
USE_OLLAMA = True

# v1.3 model routing. All roles default to the same local model for now.
# TODO v1.3 local model split:
# - planner: fast planning model such as llama3.1:8b or mistral
# - coder: code-specialized model such as qwen2.5-coder:7b/14b when installed
# - verifier: stronger reasoning/checking model when available
# - builder: balanced general model
ENABLE_MODEL_ROUTING = True
DEFAULT_MODEL_PROVIDER = "ollama"
MODEL_ROUTING_EXAMPLES = {
    "single_model_safe": {
        "planner": {"provider": "ollama", "model": OLLAMA_MODEL},
        "researcher": {"provider": "ollama", "model": OLLAMA_MODEL},
        "coder": {"provider": "ollama", "model": OLLAMA_MODEL},
        "builder": {"provider": "ollama", "model": OLLAMA_MODEL},
        "verifier": {"provider": "ollama", "model": OLLAMA_MODEL},
    },
    "local_coder_split_example": {
        "planner": {"provider": "ollama", "model": "llama3.1:8b"},
        "researcher": {"provider": "ollama", "model": "llama3.1:8b"},
        "coder": {"provider": "ollama", "model": "qwen2.5-coder:7b"},
        "builder": {"provider": "ollama", "model": "llama3.1:8b"},
        "verifier": {"provider": "ollama", "model": "llama3.1:8b"},
        "web": {"provider": "ollama", "model": "llama3.1:8b"},
    },
}
MODEL_ROUTING = {
    "planner": {"provider": "ollama", "model": OLLAMA_MODEL},
    "researcher": {"provider": "ollama", "model": OLLAMA_MODEL},
    "coder": {"provider": "ollama", "model": OLLAMA_MODEL},
    "builder": {"provider": "ollama", "model": OLLAMA_MODEL},
    "verifier": {"provider": "ollama", "model": OLLAMA_MODEL},
    "web": {"provider": "ollama", "model": OLLAMA_MODEL},
    "fallback": {"provider": "ollama", "model": OLLAMA_MODEL},
}

MAX_GOAL_LENGTH = 2000
MIN_DYNAMIC_TASKS = 3
MAX_DYNAMIC_TASKS = 7
MAX_MISSION_SECONDS = 600
MAX_TASK_SECONDS = 240
TASK_TIMEOUT_SWEEP_SECONDS = 0.25
ENABLE_TASK_METRICS = True

ENABLE_FILE_TOOLS = True
ENABLE_SHELL_TOOL = False

# v1.0/v1.3 write gates. Keep OFF unless intentionally testing.
ENABLE_PATCH_APPLICATION = False
ENABLE_FILE_WRITING = False

# v1.3 swarm execution.
ENABLE_PARALLEL_EXECUTION = True
MAX_PARALLEL_WORKERS = 3
ENABLE_AUTO_DEPENDENCY_WIRING = True

# v1.3 memory search.
ENABLE_FTS_MEMORY = True

# v1.3 external research layer.
# Read-only web search is OFF by default for safety/reproducibility.
# Enable intentionally when you want ANTHILL to use public web context.
ENABLE_WEB_SEARCH = False
WEB_SEARCH_PROVIDER = "duckduckgo_html"
MAX_WEB_RESULTS = 5
WEB_SEARCH_TIMEOUT_SECONDS = 12
MAX_SOURCE_SUMMARY_CHARS = 900
MAX_WEB_SEARCHES_PER_MISSION = 3
MAX_SOURCES_PER_MISSION = 15
MAX_SOURCES_PER_SEARCH = 5
MAX_WEB_CONTEXT_CHARS = 4000
SOURCE_LIST_LIMIT_DEFAULT = 20
SOURCE_QUALITY_LIST_LIMIT_DEFAULT = 20
SOURCE_ID_MAX_CHARS = 80

# v1.3.1 source safety/scoring. These are advisory by default, not hard censorship.
# Allowlist domains receive an authority boost. Blocklist domains are skipped.
SOURCE_ALLOWLIST_DOMAINS = {
    "docs.python.org",
    "github.com",
    "microsoft.com",
    "openai.com",
    "nist.gov",
    "cisa.gov",
}
SOURCE_BLOCKLIST_DOMAINS = {
    "pinterest.com",
}
HIGH_AUTHORITY_DOMAIN_SUFFIXES = {
    ".gov",
    ".edu",
}
HIGH_AUTHORITY_DOMAIN_KEYWORDS = {
    "docs.",
    "developer.",
    "github.com",
    "stackoverflow.com",
    "microsoft.com",
    "openai.com",
    "nvidia.com",
    "dell.com",
}
WEB_SEARCH_KEYWORDS = {
    "latest", "current", "today", "news", "recent", "web", "internet",
    "search", "lookup", "look up", "online", "price", "version", "docs",
    "documentation", "advisory", "security advisory", "cve", "release",
}

# v1.3 message efficiency + scaling prep.
# This keeps ant communication as normal Python/string/JSON for now,
# but prevents uncontrolled context bloat by summarizing and budgeting messages.
ENABLE_CONTEXT_PACKETS = True
ENABLE_RESULT_SUMMARIES = True
ENABLE_MESSAGE_METRICS = True
MAX_CONTEXT_PACKET_CHARS = 7000
MAX_CONTEXT_ITEM_CHARS = 1600
MAX_CONTEXT_SUMMARY_CHARS = 700
MAX_RESULT_SUMMARY_CHARS = 900
MAX_CONTEXT_ITEMS_PER_PACKET = 8
RAW_CONTEXT_ROLES = {"coder": {"file", "researcher"}, "builder": {"coder", "file"}, "verifier": {"builder", "coder"}}
TOKEN_ESTIMATE_CHARS_PER_TOKEN = 4

# v1.3 observability and runtime diagnostics.
EVENT_LIST_LIMIT_DEFAULT = 30
DIAGNOSTIC_EVENT_LIMIT = 12
FAILURE_EVENT_TYPES = {
    "task_failed",
    "tool_failed",
    "patch_apply_failed",
    "patch_proposal_parse_failed",
    "mission_timeout",
    "task_timeout",
    "model_call_failed",
}

MAX_FILE_READ_CHARS = 5000
MAX_DIRECTORY_ITEMS = 100
MAX_PREVIOUS_CONTEXT_CHARS = 4000
MAX_VERIFIER_CONTEXT_CHARS = 5000
MAX_CODER_CONTEXT_CHARS = 6000
MAX_FILEANT_FILES_TO_READ = 3

SHOW_FULL_DEBUG_TRACE_IN_CLI = False
MAX_CLI_DEBUG_TRACE_CHARS = 5000

RECENT_MEMORY_LIMIT = 3
RELEVANT_MEMORY_LIMIT = 5
MEMORY_RESULT_CHARS = 400

MAX_PATCH_PROPOSALS_PER_SET = 10
MAX_PATCH_CONTENT_CHARS = 8000
MAX_PATCH_DISPLAY_CHARS = 12000

HISTORY_LIMIT_DEFAULT = 10
PATCH_LIST_LIMIT_DEFAULT = 20
MEMORY_LIST_LIMIT_DEFAULT = 10
PHEROMONE_LIST_LIMIT_DEFAULT = 15
APPROVAL_ID_MAX_CHARS = 80
PATCH_ID_MAX_CHARS = 80

ALLOWED_WORKSPACE_ROOT = "."
BACKUP_DIR = "data/backups"

BLOCKED_FILE_SUFFIXES = {".db", ".sqlite", ".sqlite3"}
BLOCKED_PATH_PARTS = {
    "data", ".git", "__pycache__", ".venv", "venv", "env",
    ".mypy_cache", ".pytest_cache",
}

PATCH_ALLOWED_SUFFIXES = {
    ".txt", ".md", ".py", ".json", ".yaml", ".yml", ".toml",
    ".ini", ".cfg", ".log", ".csv", ".html", ".css", ".js",
    ".ts", ".tsx", ".jsx", ".xml",
}


# ============================================================
#  HELPERS
# ============================================================

def now_utc() -> datetime:
    return datetime.now(timezone.utc)


def timestamp_id() -> str:
    return now_utc().strftime("%Y%m%dT%H%M%SZ")


def safe_json_dumps(data: Any) -> str:
    return json.dumps(data, default=str)


def pydantic_deep_copy(model):
    # Supports both Pydantic v1 (.copy) and v2 (.model_copy).
    if hasattr(model, "model_copy"):
        return model.model_copy(deep=True)
    return model.copy(deep=True)


def resolve_workspace_root() -> Path:
    root_path = Path(ALLOWED_WORKSPACE_ROOT)
    return root_path.resolve() if root_path.is_absolute() else (SCRIPT_DIR / root_path).resolve()


def truncate_text(text: Optional[str], max_chars: int, suffix: str = "...[truncated]") -> str:
    if text is None:
        return ""
    if len(text) <= max_chars:
        return text
    return text[:max_chars].rstrip() + f"\n{suffix}"


def estimate_token_count(text: Optional[str]) -> int:
    if not text:
        return 0
    return max(1, int(len(str(text)) / TOKEN_ESTIMATE_CHARS_PER_TOKEN))


def compact_whitespace(text: str) -> str:
    return re.sub(r"\n{3,}", "\n\n", text.strip())


def create_result_summary(text: Optional[str], max_chars: int = MAX_RESULT_SUMMARY_CHARS) -> str:
    if not text:
        return ""
    cleaned = compact_whitespace(str(text))
    # Prefer leading content because most ants put summaries first.
    # Later ANTHILL can replace this with model-generated or embedding-backed summaries.
    return truncate_text(cleaned, max_chars, suffix="...[summary truncated]")


def build_context_packet_text(
    mission: "Mission",
    consumer_role: str,
    max_total_chars: int,
    max_item_chars: int = MAX_CONTEXT_ITEM_CHARS,
) -> str:
    if not ENABLE_CONTEXT_PACKETS:
        raw_blocks = []
        for item in mission.tasks:
            if item.result:
                raw_blocks.append(
                    f"Task: {item.title}\nAnt: {item.assigned_ant}\nTask Type: {item.task_type}\n"
                    f"Status: {item.status.value}\nResult:\n{item.result}"
                )
        return truncate_text("\n\n---\n\n".join(raw_blocks), max_total_chars, suffix="...[context truncated]")

    allowed_raw_roles = RAW_CONTEXT_ROLES.get(consumer_role, set())
    blocks = [
        "CONTEXT PACKET",
        f"Mission ID: {mission.id}",
        f"Consumer Role: {consumer_role}",
        f"Mission Goal: {truncate_text(mission.goal, 500, suffix='...[goal truncated]')}",
        "Mode: compact summaries with selective raw extracts",
    ]

    included = 0
    for item in mission.tasks:
        if not item.result or item.status not in {TaskStatus.COMPLETE, TaskStatus.FAILED, TaskStatus.SKIPPED}:
            continue
        if included >= MAX_CONTEXT_ITEMS_PER_PACKET:
            blocks.append(f"...[context item limit reached: {MAX_CONTEXT_ITEMS_PER_PACKET}]")
            break

        summary = item.result_summary or create_result_summary(item.result, MAX_CONTEXT_SUMMARY_CHARS)
        block = (
            f"Task ID: {item.id}\n"
            f"Title: {item.title}\n"
            f"Ant: {item.assigned_ant}\n"
            f"Task Type: {item.task_type}\n"
            f"Status: {item.status.value}\n"
            f"Result Summary:\n{summary}"
        )

        if item.assigned_ant in allowed_raw_roles:
            raw = truncate_text(item.result or "", max_item_chars, suffix="...[raw extract truncated]")
            block += f"\nRaw Extract:\n{raw}"

        blocks.append(block)
        included += 1

    packet = "\n\n---\n\n".join(blocks)
    return truncate_text(packet, max_total_chars, suffix="...[context packet truncated]")


def is_valid_uuid(value: str) -> bool:
    try:
        UUID(str(value))
        return True
    except Exception:
        return False


def validate_uuid_id(value: str, label: str, max_chars: int = 80) -> str:
    cleaned = str(value).strip()
    if not cleaned:
        raise ValueError(f"Missing {label}.")
    if len(cleaned) > max_chars:
        raise ValueError(f"{label} is too long.")
    if not is_valid_uuid(cleaned):
        raise ValueError(f"{label} must be a valid UUID.")
    return cleaned


def validate_approval_id(value: str) -> str:
    return validate_uuid_id(value, "approval id", APPROVAL_ID_MAX_CHARS)


def validate_patch_id(value: str) -> str:
    return validate_uuid_id(value, "patch id", PATCH_ID_MAX_CHARS)


def validate_source_id(value: str) -> str:
    # v1.3.2+ source ids are deterministic src_<24hex> strings, not UUIDs.
    cleaned = str(value).strip()
    if not cleaned:
        raise ValueError("Missing source id.")
    if len(cleaned) > SOURCE_ID_MAX_CHARS:
        raise ValueError("source id is too long.")
    if not re.fullmatch(r"src_[0-9a-f]{24}", cleaned):
        raise ValueError("source id must match src_<24hexchars>.")
    return cleaned


def extract_keywords(text: str) -> set[str]:
    words = re.findall(r"[a-zA-Z0-9_]+", text.lower())
    stopwords = {
        "the", "and", "for", "with", "this", "that", "from", "into", "have",
        "what", "when", "where", "which", "would", "should", "could", "mission",
        "task", "result", "about", "your", "you", "are", "was", "were", "how",
    }
    return {word for word in words if len(word) > 3 and word not in stopwords}


def should_use_web_search(goal: str) -> bool:
    lowered = goal.lower()
    return any(keyword in lowered for keyword in WEB_SEARCH_KEYWORDS)


def strip_html_tags(html_text: str) -> str:
    text = re.sub(r"<script.*?</script>", " ", html_text, flags=re.DOTALL | re.IGNORECASE)
    text = re.sub(r"<style.*?</style>", " ", text, flags=re.DOTALL | re.IGNORECASE)
    text = re.sub(r"<[^>]+>", " ", text)
    text = text.replace("&amp;", "&").replace("&quot;", '"').replace("&#x27;", "'")
    text = text.replace("&lt;", "<").replace("&gt;", ">")
    return compact_whitespace(text)


def decode_search_url(url: str) -> str:
    cleaned = str(url or "").strip()
    if not cleaned:
        return cleaned
    if cleaned.startswith("//"):
        cleaned = "https:" + cleaned
    try:
        parsed = urlparse(cleaned)
        query = parse_qs(parsed.query)
        if "uddg" in query and query["uddg"]:
            return unquote(query["uddg"][0])
    except Exception:
        pass
    return cleaned


def extract_domain(url: str) -> str:
    cleaned = decode_search_url(url)
    try:
        parsed = urlparse(cleaned)
        domain = parsed.netloc.lower()
        if domain.startswith("www."):
            domain = domain[4:]
        return domain or "unknown"
    except Exception:
        match = re.match(r"https?://([^/]+)", str(url).strip())
        return match.group(1).lower() if match else "unknown"


def normalize_url_for_dedupe(url: str) -> str:
    cleaned = decode_search_url(url).strip()
    try:
        parsed = urlparse(cleaned)
        domain = parsed.netloc.lower().removeprefix("www.")
        path = parsed.path.rstrip("/")
        return f"{parsed.scheme.lower()}://{domain}{path}"
    except Exception:
        return cleaned.lower().rstrip("/")


def source_id_from_url(url: str) -> str:
    # v1.3.2: deterministic source ids keep the same normalized URL from
    # becoming a different random source every time ANTHILL sees it.
    normalized = normalize_url_for_dedupe(url)
    digest = hashlib.sha256(normalized.encode("utf-8", errors="ignore")).hexdigest()[:24]
    return f"src_{digest}"


def extract_verdict(text: str) -> str:
    lowered = text.lower()
    for line in lowered.splitlines():
        clean = line.strip().replace("*", "").replace("-", "").strip()
        if clean.startswith("verdict:"):
            verdict = clean.replace("verdict:", "", 1).strip()
            if "verification failed" in verdict or "failed" in verdict:
                return "failed"
            if "needs improvement" in verdict or "improvement" in verdict:
                return "needs_improvement"
            if "verification passed" in verdict or "passed" in verdict:
                return "passed"
    if "verification failed" in lowered or "failed verification" in lowered:
        return "failed"
    if "needs improvement" in lowered:
        return "needs_improvement"
    if "verification passed" in lowered or "passed verification" in lowered:
        return "passed"
    return "unknown"


def infer_task_type(assigned_ant: str, title: str = "", description: str = "") -> str:
    if assigned_ant == "researcher":
        return "research"
    if assigned_ant == "file":
        return "file_inspection"
    if assigned_ant == "coder":
        return "patch_proposal"
    if assigned_ant == "builder":
        return "build_answer"
    if assigned_ant == "verifier":
        return "verification"
    if assigned_ant == "web":
        return "external_research"
    return "general"


def extract_json_object(text: str) -> Dict[str, Any]:
    cleaned = text.strip()
    if cleaned.startswith("```"):
        cleaned = re.sub(r"^```(?:json)?", "", cleaned, flags=re.IGNORECASE).strip()
        cleaned = re.sub(r"```$", "", cleaned).strip()
    try:
        return json.loads(cleaned)
    except json.JSONDecodeError:
        match = re.search(r"\{.*\}", cleaned, re.DOTALL)
        if not match:
            raise ValueError("No JSON object found.")
        return json.loads(match.group(0))


def validate_safe_patch_path(file_path: str) -> str:
    cleaned = str(file_path).strip()
    if not cleaned:
        raise ValueError("Patch proposal missing file_path.")
    path = Path(cleaned)
    if path.is_absolute():
        raise ValueError(f"Patch file_path must be relative, not absolute: {cleaned}")
    if ".." in path.parts:
        raise ValueError(f"Patch file_path cannot contain '..': {cleaned}")
    lowered_parts = {part.lower() for part in path.parts}
    if lowered_parts.intersection(BLOCKED_PATH_PARTS):
        raise ValueError(f"Patch file_path targets blocked internal path: {cleaned}")
    if path.suffix.lower() in BLOCKED_FILE_SUFFIXES:
        raise ValueError(f"Patch file_path targets blocked file type: {path.suffix}")
    if path.suffix.lower() not in PATCH_ALLOWED_SUFFIXES:
        raise ValueError(f"Patch file_path has unsupported file type: {path.suffix}")
    return cleaned


# ============================================================
#  MODELS
# ============================================================

class TaskStatus(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    COMPLETE = "complete"
    FAILED = "failed"
    SKIPPED = "skipped"


class MissionStatus(str, Enum):
    CREATED = "created"
    RUNNING = "running"
    COMPLETE = "complete"
    PARTIAL = "partial"
    FAILED = "failed"


class PatchChangeType(str, Enum):
    ADD = "add"
    MODIFY = "modify"
    DELETE = "delete"
    RENAME = "rename"


class PatchStatus(str, Enum):
    PROPOSED = "proposed"
    APPROVED = "approved"
    REJECTED = "rejected"
    APPLIED = "applied"
    FAILED = "failed"


class ApprovalStatus(str, Enum):
    PENDING = "pending"
    APPROVED = "approved"
    REJECTED = "rejected"
    EXPIRED = "expired"
    CONSUMED = "consumed"


class ApprovalActionType(str, Enum):
    PATCH_PROPOSAL = "patch_proposal"
    FILE_WRITE = "file_write"
    SHELL_COMMAND = "shell_command"
    TOOL_USE = "tool_use"


class Task(BaseModel):
    # ANTHILL alignment:
    # A Task is a single tunnel segment in the mission path.
    # The Queen assigns it to one specialized ant, then memory records the result.
    id: str = Field(default_factory=lambda: str(uuid4()))
    title: str
    description: str
    assigned_ant: str
    task_type: str = "general"
    parent_task_ids: List[str] = Field(default_factory=list)
    depends_on: List[str] = Field(default_factory=list)
    status: TaskStatus = TaskStatus.PENDING
    result: Optional[str] = None
    # v1.3 scaling prep: preserve full result but pass summaries/context packets to later ants.
    result_summary: Optional[str] = None
    result_chars: int = 0
    estimated_tokens: int = 0
    # v1.3 parallel hardening: task timing is first-class telemetry.
    started_at: Optional[datetime] = None
    finished_at: Optional[datetime] = None
    elapsed_seconds: Optional[float] = None


class Mission(BaseModel):
    # ANTHILL alignment:
    # A Mission is the user request as understood by the Queen.
    # It becomes a task path, gets executed by ants, verified, scored, and saved.
    id: str = Field(default_factory=lambda: str(uuid4()))
    goal: str
    tasks: List[Task] = Field(default_factory=list)
    status: MissionStatus = MissionStatus.CREATED
    user_result: Optional[str] = None
    debug_result: Optional[str] = None
    final_result: Optional[str] = None
    best_output_task_id: Optional[str] = None
    success_score: Optional[float] = None
    created_at: datetime = Field(default_factory=now_utc)


class Event(BaseModel):
    # ANTHILL alignment:
    # Events are the observable activity stream that a future UI can render live.
    id: str = Field(default_factory=lambda: str(uuid4()))
    mission_id: str
    task_id: Optional[str] = None
    ant_name: Optional[str] = None
    event_type: str
    message: str
    metadata: Dict[str, Any] = Field(default_factory=dict)
    created_at: datetime = Field(default_factory=now_utc)


class ToolResult(BaseModel):
    tool_name: str
    success: bool
    output: str
    error: Optional[str] = None


class TaskResultSummary(BaseModel):
    task_id: str
    mission_id: str
    ant_name: str
    task_type: str
    status: str
    summary: str
    result_chars: int
    estimated_tokens: int
    created_at: datetime = Field(default_factory=now_utc)


class ContextPacket(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    mission_id: str
    consumer_role: str
    task_ids: List[str] = Field(default_factory=list)
    total_chars: int = 0
    estimated_tokens: int = 0
    content: str
    created_at: datetime = Field(default_factory=now_utc)


class SearchResult(BaseModel):
    title: str
    url: str
    snippet: str = ""
    source: str = "web"


class SourceRecord(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    mission_id: str
    task_id: Optional[str] = None
    ant_name: Optional[str] = None
    title: str
    url: str
    domain: str
    snippet: str = ""
    summary: str = ""
    provider: str = WEB_SEARCH_PROVIDER
    relevance_score: float = 0.0
    freshness_score: float = 0.0
    authority_score: float = 0.0
    confidence_score: float = 0.0
    confidence_label: str = "unknown"
    quality_notes: str = ""
    created_at: datetime = Field(default_factory=now_utc)


class PatchProposal(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    file_path: str
    change_type: PatchChangeType
    reason: str
    risk: str
    old_content: Optional[str] = None
    new_content: Optional[str] = None
    requires_approval: bool = True
    status: PatchStatus = PatchStatus.PROPOSED
    created_at: datetime = Field(default_factory=now_utc)


class PatchSet(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    mission_id: str
    task_id: str
    summary: str
    proposals: List[PatchProposal] = Field(default_factory=list)
    created_at: datetime = Field(default_factory=now_utc)


class ApprovalRequest(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    mission_id: str
    task_id: Optional[str] = None
    action_type: ApprovalActionType
    target_id: str
    title: str
    description: str
    status: ApprovalStatus = ApprovalStatus.PENDING
    requested_by: str = "queen"
    decision_note: Optional[str] = None
    metadata: Dict[str, Any] = Field(default_factory=dict)
    created_at: datetime = Field(default_factory=now_utc)
    decided_at: Optional[datetime] = None


# ============================================================
#  OLLAMA CLIENT
# ============================================================

class OllamaClient:
    def __init__(self, model: str = OLLAMA_MODEL, host: str = OLLAMA_HOST):
        self.model = model
        self.host = host.rstrip("/")

    def generate(self, prompt: str, retries: int = 2) -> str:
        url = f"{self.host}/api/generate"
        payload = {"model": self.model, "prompt": prompt, "stream": False}
        last_error = ""
        for attempt in range(1, retries + 1):
            try:
                response = requests.post(url, json=payload, timeout=180)
                response.raise_for_status()
                data = response.json()
                output = data.get("response", "").strip()
                return output or "Ollama returned an empty response."
            except requests.exceptions.ConnectionError:
                return "ERROR: Could not connect to Ollama. Make sure Ollama is running at http://localhost:11434."
            except requests.exceptions.Timeout:
                last_error = f"ERROR: Ollama request timed out (attempt {attempt}/{retries})."
            except Exception as error:
                last_error = f"ERROR: Ollama request failed: {error} (attempt {attempt}/{retries})."
        return last_error


class OpenAIClientPlaceholder:
    def generate(self, prompt: str, retries: int = 2) -> str:
        return "ERROR: OpenAI provider placeholder is not implemented in v1.3."


class AnthropicClientPlaceholder:
    def generate(self, prompt: str, retries: int = 2) -> str:
        return "ERROR: Anthropic provider placeholder is not implemented in v1.3."


class OpenRouterClientPlaceholder:
    def generate(self, prompt: str, retries: int = 2) -> str:
        return "ERROR: OpenRouter provider placeholder is not implemented in v1.3."


class ModelRouter:
    # v1.3 routing layer:
    # Ant communication is still Python object/string/JSON based, not binary or quantized.
    # Scaling now comes from compact context packets, model routing, and message metrics.
    def __init__(self, memory: Optional["SQLiteMemory"] = None):
        self.memory = memory
        self.clients: Dict[str, Any] = {}
        self.call_count = 0
        self.lock = Lock()

    def get_route(self, role: str) -> Dict[str, str]:
        route = MODEL_ROUTING.get(role) or MODEL_ROUTING.get("fallback") or {"provider": DEFAULT_MODEL_PROVIDER, "model": OLLAMA_MODEL}
        return {
            "provider": str(route.get("provider", DEFAULT_MODEL_PROVIDER)),
            "model": str(route.get("model", OLLAMA_MODEL)),
        }

    def _client_key(self, provider: str, model: str) -> str:
        return f"{provider}:{model}"

    def get_client(self, provider: str, model: str):
        key = self._client_key(provider, model)
        if key in self.clients:
            return self.clients[key]
        if provider == "ollama":
            client = OllamaClient(model=model)
        elif provider == "openai":
            client = OpenAIClientPlaceholder()
        elif provider == "anthropic":
            client = AnthropicClientPlaceholder()
        elif provider == "openrouter":
            client = OpenRouterClientPlaceholder()
        else:
            client = OllamaClient(model=OLLAMA_MODEL)
        self.clients[key] = client
        return client

    def generate(
        self,
        role: str,
        prompt: str,
        mission_id: Optional[str] = None,
        task_id: Optional[str] = None,
        ant_name: Optional[str] = None,
        retries: int = 2,
    ) -> str:
        if not USE_OLLAMA and DEFAULT_MODEL_PROVIDER == "ollama":
            return "ERROR: Model routing requested Ollama, but USE_OLLAMA is False."

        route = self.get_route(role)
        provider = route["provider"]
        model = route["model"]
        client = self.get_client(provider, model)
        started = time.time()
        response = client.generate(prompt, retries=retries)
        duration_ms = int((time.time() - started) * 1000)
        success = not response.startswith("ERROR:")
        if success:
            pheromone_delta = 0.01
        elif "timed out" in response.lower():
            pheromone_delta = -0.02
        else:
            # v1.3: soften generic availability failures so flaky local services do not
            # permanently poison otherwise-good model routes. Quality failures can be
            # penalized more strongly later through verifier/mission outcomes.
            pheromone_delta = -0.01

        with self.lock:
            self.call_count += 1

        if self.memory and mission_id:
            self.memory.log_event(
                mission_id=mission_id,
                task_id=task_id,
                ant_name=ant_name or role,
                event_type="model_call",
                message=f"Model call for role {role}: {provider}/{model}",
                metadata={
                    "role": role,
                    "provider": provider,
                    "model": model,
                    "success": success,
                    "duration_ms": duration_ms,
                    "prompt_chars": len(prompt),
                    "response_chars": len(response),
                    "pheromone_delta": pheromone_delta,
                },
            )
            self.memory.update_pheromone_trail(
                trail_key=f"model:{provider}:{model}:{role}",
                trail_type="model_route",
                success=success,
                strength_delta=pheromone_delta,
                metadata={
                    "role": role,
                    "provider": provider,
                    "model": model,
                    "duration_ms": duration_ms,
                    "last_mission_id": mission_id,
                    "last_task_id": task_id,
                },
            )
        return response

    def format_routes(self) -> str:
        lines = ["ANTHILL v1.4 Model Routes"]
        for role in ["planner", "researcher", "web", "coder", "builder", "verifier", "fallback"]:
            route = self.get_route(role)
            lines.append(f"{role}: provider={route['provider']} | model={route['model']}")
        return "\n".join(lines)

    def format_models(self) -> str:
        active = sorted(set(f"{self.get_route(role)['provider']}:{self.get_route(role)['model']}" for role in MODEL_ROUTING))
        return (
            "ANTHILL v1.4 Model Router\n"
            f"Routing Enabled: {'ON' if ENABLE_MODEL_ROUTING else 'OFF'}\n"
            f"Default Provider: {DEFAULT_MODEL_PROVIDER}\n"
            f"Ollama Host: {OLLAMA_HOST}\n"
            f"Total Model Calls This Session: {self.call_count}\n"
            f"Active Route Targets: {', '.join(active)}\n"
            "Provider Status: Ollama active; OpenAI/Anthropic/OpenRouter placeholders only in v1.3."
        )


# ============================================================
#  MEMORY
# ============================================================

class SQLiteMemory:
    def __init__(self, db_path: str = "data/anthill_memory.db"):
        self.db_path = str((SCRIPT_DIR / db_path).resolve())
        Path(self.db_path).parent.mkdir(parents=True, exist_ok=True)
        self.fts_available = False
        self._init_db()

    def _connect(self):
        conn = sqlite3.connect(self.db_path, timeout=30, check_same_thread=False)
        conn.execute("PRAGMA journal_mode=WAL")
        conn.execute("PRAGMA busy_timeout=30000")
        return conn

    def _init_db(self):
        with self._connect() as conn:
            cursor = conn.cursor()

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS missions (
                    id TEXT PRIMARY KEY,
                    goal TEXT NOT NULL,
                    status TEXT NOT NULL,
                    user_result TEXT,
                    debug_result TEXT,
                    final_result TEXT,
                    best_output_task_id TEXT,
                    success_score REAL,
                    created_at TEXT NOT NULL,
                    saved_at TEXT NOT NULL
                )
            """)

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS tasks (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    description TEXT NOT NULL,
                    assigned_ant TEXT NOT NULL,
                    task_type TEXT NOT NULL,
                    parent_task_ids_json TEXT,
                    depends_on_json TEXT,
                    status TEXT NOT NULL,
                    result TEXT,
                    result_summary TEXT,
                    result_chars INTEGER DEFAULT 0,
                    estimated_tokens INTEGER DEFAULT 0,
                    started_at TEXT,
                    finished_at TEXT,
                    elapsed_seconds REAL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                )
            """)

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS events (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    task_id TEXT,
                    ant_name TEXT,
                    event_type TEXT NOT NULL,
                    message TEXT NOT NULL,
                    metadata_json TEXT,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                )
            """)

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS pheromone_trails (
                    id TEXT PRIMARY KEY,
                    trail_key TEXT UNIQUE NOT NULL,
                    trail_type TEXT NOT NULL,
                    strength REAL NOT NULL,
                    success_count INTEGER NOT NULL,
                    failure_count INTEGER NOT NULL,
                    last_updated TEXT NOT NULL,
                    metadata_json TEXT
                )
            """)

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS patch_sets (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    task_id TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    proposal_count INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id),
                    FOREIGN KEY (task_id) REFERENCES tasks(id)
                )
            """)

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS patch_proposals (
                    id TEXT PRIMARY KEY,
                    patch_set_id TEXT NOT NULL,
                    mission_id TEXT NOT NULL,
                    task_id TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    change_type TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    risk TEXT NOT NULL,
                    old_content TEXT,
                    new_content TEXT,
                    requires_approval INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    applied_at TEXT,
                    backup_path TEXT,
                    last_error TEXT,
                    FOREIGN KEY (mission_id) REFERENCES missions(id),
                    FOREIGN KEY (task_id) REFERENCES tasks(id),
                    FOREIGN KEY (patch_set_id) REFERENCES patch_sets(id)
                )
            """)

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS approval_requests (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    task_id TEXT,
                    action_type TEXT NOT NULL,
                    target_id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    description TEXT NOT NULL,
                    status TEXT NOT NULL,
                    requested_by TEXT NOT NULL,
                    decision_note TEXT,
                    metadata_json TEXT,
                    created_at TEXT NOT NULL,
                    decided_at TEXT,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                )
            """)

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS task_result_summaries (
                    task_id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    ant_name TEXT NOT NULL,
                    task_type TEXT NOT NULL,
                    status TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    result_chars INTEGER NOT NULL,
                    estimated_tokens INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                )
            """)

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS message_metrics (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    task_id TEXT,
                    ant_name TEXT,
                    metric_type TEXT NOT NULL,
                    input_chars INTEGER NOT NULL,
                    output_chars INTEGER NOT NULL,
                    input_tokens_est INTEGER NOT NULL,
                    output_tokens_est INTEGER NOT NULL,
                    metadata_json TEXT,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                )
            """)

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS source_records (
                    id TEXT PRIMARY KEY,
                    mission_id TEXT NOT NULL,
                    task_id TEXT,
                    ant_name TEXT,
                    title TEXT NOT NULL,
                    url TEXT NOT NULL,
                    domain TEXT NOT NULL,
                    snippet TEXT,
                    summary TEXT,
                    provider TEXT NOT NULL,
                    relevance_score REAL DEFAULT 0,
                    freshness_score REAL DEFAULT 0,
                    authority_score REAL DEFAULT 0,
                    confidence_score REAL DEFAULT 0,
                    confidence_label TEXT DEFAULT 'unknown',
                    quality_notes TEXT,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                )
            """)

            if ENABLE_FTS_MEMORY:
                try:
                    cursor.execute("""
                        CREATE VIRTUAL TABLE IF NOT EXISTS missions_fts
                        USING fts5(id UNINDEXED, goal, user_result, final_result)
                    """)
                    self.fts_available = True
                except sqlite3.OperationalError:
                    self.fts_available = False

            conn.commit()
            self._ensure_columns(conn)

    def _ensure_columns(self, conn):
        cursor = conn.cursor()

        def columns_for(table_name: str) -> set[str]:
            cursor.execute(f"PRAGMA table_info({table_name})")
            return {row[1] for row in cursor.fetchall()}

        mission_columns = columns_for("missions")
        for column, column_type in {
            "user_result": "TEXT",
            "debug_result": "TEXT",
            "best_output_task_id": "TEXT",
        }.items():
            if column not in mission_columns:
                cursor.execute(f"ALTER TABLE missions ADD COLUMN {column} {column_type}")

        task_columns = columns_for("tasks")
        for column, column_type in {
            "task_type": "TEXT DEFAULT 'general'",
            "parent_task_ids_json": "TEXT",
            "depends_on_json": "TEXT",
            "result_summary": "TEXT",
            "result_chars": "INTEGER DEFAULT 0",
            "estimated_tokens": "INTEGER DEFAULT 0",
            "started_at": "TEXT",
            "finished_at": "TEXT",
            "elapsed_seconds": "REAL",
        }.items():
            if column not in task_columns:
                cursor.execute(f"ALTER TABLE tasks ADD COLUMN {column} {column_type}")

        patch_columns = columns_for("patch_proposals")
        for column, column_type in {
            "applied_at": "TEXT",
            "backup_path": "TEXT",
            "last_error": "TEXT",
        }.items():
            if column not in patch_columns:
                cursor.execute(f"ALTER TABLE patch_proposals ADD COLUMN {column} {column_type}")

        source_columns = columns_for("source_records")
        for column, column_type in {
            "relevance_score": "REAL DEFAULT 0",
            "freshness_score": "REAL DEFAULT 0",
            "authority_score": "REAL DEFAULT 0",
            "confidence_score": "REAL DEFAULT 0",
            "confidence_label": "TEXT DEFAULT 'unknown'",
            "quality_notes": "TEXT",
        }.items():
            if column not in source_columns:
                cursor.execute(f"ALTER TABLE source_records ADD COLUMN {column} {column_type}")

        conn.commit()

    def _sync_mission_fts(self, cursor, mission: Mission):
        if not self.fts_available:
            return
        try:
            cursor.execute("DELETE FROM missions_fts WHERE id = ?", (mission.id,))
            cursor.execute(
                "INSERT INTO missions_fts (id, goal, user_result, final_result) VALUES (?, ?, ?, ?)",
                (mission.id, mission.goal, mission.user_result or "", mission.final_result or ""),
            )
        except sqlite3.OperationalError:
            self.fts_available = False

    def save_mission(self, mission: Mission):
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT OR REPLACE INTO missions (
                    id, goal, status, user_result, debug_result, final_result,
                    best_output_task_id, success_score, created_at, saved_at
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, (
                mission.id, mission.goal, mission.status.value, mission.user_result,
                mission.debug_result, mission.final_result, mission.best_output_task_id,
                mission.success_score, mission.created_at.isoformat(), now_utc().isoformat(),
            ))

            for task in mission.tasks:
                cursor.execute("""
                    INSERT OR REPLACE INTO tasks (
                        id, mission_id, title, description, assigned_ant, task_type,
                        parent_task_ids_json, depends_on_json, status, result,
                        result_summary, result_chars, estimated_tokens,
                        started_at, finished_at, elapsed_seconds
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """, (
                    task.id, mission.id, task.title, task.description, task.assigned_ant,
                    task.task_type, safe_json_dumps(task.parent_task_ids),
                    safe_json_dumps(task.depends_on), task.status.value, task.result,
                    task.result_summary, task.result_chars, task.estimated_tokens,
                    task.started_at.isoformat() if task.started_at else None,
                    task.finished_at.isoformat() if task.finished_at else None,
                    task.elapsed_seconds,
                ))

            self._sync_mission_fts(cursor, mission)
            conn.commit()

    def save_patch_set(self, patch_set: PatchSet):
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT OR REPLACE INTO patch_sets (
                    id, mission_id, task_id, summary, proposal_count, created_at
                )
                VALUES (?, ?, ?, ?, ?, ?)
            """, (
                patch_set.id, patch_set.mission_id, patch_set.task_id,
                patch_set.summary, len(patch_set.proposals), patch_set.created_at.isoformat(),
            ))

            for proposal in patch_set.proposals:
                cursor.execute("""
                    INSERT OR REPLACE INTO patch_proposals (
                        id, patch_set_id, mission_id, task_id, file_path,
                        change_type, reason, risk, old_content, new_content,
                        requires_approval, status, created_at
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """, (
                    proposal.id, patch_set.id, patch_set.mission_id, patch_set.task_id,
                    proposal.file_path, proposal.change_type.value, proposal.reason, proposal.risk,
                    proposal.old_content, proposal.new_content, 1 if proposal.requires_approval else 0,
                    proposal.status.value, proposal.created_at.isoformat(),
                ))
            conn.commit()

    def save_task_result_summary(self, mission_id: str, task: Task):
        if not ENABLE_RESULT_SUMMARIES:
            return
        summary = task.result_summary or create_result_summary(task.result)
        task.result_summary = summary
        task.result_chars = len(task.result or "")
        task.estimated_tokens = estimate_token_count(task.result)
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT OR REPLACE INTO task_result_summaries (
                    task_id, mission_id, ant_name, task_type, status, summary,
                    result_chars, estimated_tokens, created_at
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, (
                task.id, mission_id, task.assigned_ant, task.task_type, task.status.value,
                summary, task.result_chars, task.estimated_tokens, now_utc().isoformat(),
            ))
            conn.commit()

    def log_message_metric(
        self,
        mission_id: str,
        task_id: Optional[str],
        ant_name: Optional[str],
        metric_type: str,
        input_chars: int,
        output_chars: int,
        metadata: Optional[Dict[str, Any]] = None,
    ):
        if not ENABLE_MESSAGE_METRICS:
            return
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT INTO message_metrics (
                    id, mission_id, task_id, ant_name, metric_type,
                    input_chars, output_chars, input_tokens_est, output_tokens_est,
                    metadata_json, created_at
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, (
                str(uuid4()), mission_id, task_id, ant_name, metric_type,
                int(input_chars), int(output_chars),
                estimate_token_count("x" * int(input_chars)),
                estimate_token_count("x" * int(output_chars)),
                safe_json_dumps(metadata or {}), now_utc().isoformat(),
            ))
            conn.commit()

    def save_source_record(self, source: SourceRecord):
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT OR REPLACE INTO source_records (
                    id, mission_id, task_id, ant_name, title, url, domain,
                    snippet, summary, provider, relevance_score, freshness_score,
                    authority_score, confidence_score, confidence_label, quality_notes, created_at
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, (
                source.id, source.mission_id, source.task_id, source.ant_name,
                source.title, source.url, source.domain, source.snippet,
                source.summary, source.provider, source.relevance_score, source.freshness_score,
                source.authority_score, source.confidence_score, source.confidence_label,
                source.quality_notes, source.created_at.isoformat(),
            ))
            conn.commit()

    def get_source_record(self, source_id: str) -> Optional[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM source_records WHERE id = ?", (source_id,))
            row = cursor.fetchone()
            return dict(row) if row else None

    def get_recent_sources(self, limit: int = SOURCE_LIST_LIMIT_DEFAULT) -> List[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT *
                FROM source_records
                ORDER BY created_at DESC
                LIMIT ?
            """, (limit,))
            return [dict(row) for row in cursor.fetchall()]

    def count_sources_for_mission(self, mission_id: str) -> int:
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT COUNT(*) FROM source_records WHERE mission_id = ?", (mission_id,))
            return int(cursor.fetchone()[0])

    def count_web_search_attempts_for_mission(self, mission_id: str) -> int:
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT COUNT(*) FROM events WHERE mission_id = ? AND event_type = ?",
                (mission_id, "web_search_attempted"),
            )
            return int(cursor.fetchone()[0])

    def get_source_quality_summary(self, limit: int = SOURCE_QUALITY_LIST_LIMIT_DEFAULT) -> List[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT
                    domain,
                    COUNT(*) AS source_count,
                    ROUND(AVG(relevance_score), 3) AS avg_relevance,
                    ROUND(AVG(freshness_score), 3) AS avg_freshness,
                    ROUND(AVG(authority_score), 3) AS avg_authority,
                    ROUND(AVG(confidence_score), 3) AS avg_confidence,
                    MAX(created_at) AS last_seen
                FROM source_records
                GROUP BY domain
                ORDER BY avg_confidence DESC, source_count DESC, last_seen DESC
                LIMIT ?
            """, (limit,))
            return [dict(row) for row in cursor.fetchall()]

    def get_recent_message_metrics(self, limit: int = 20) -> List[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT *
                FROM message_metrics
                ORDER BY created_at DESC
                LIMIT ?
            """, (limit,))
            return [dict(row) for row in cursor.fetchall()]

    def summarize_message_metrics(self) -> Dict[str, Any]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT
                    COUNT(*) AS metric_count,
                    COALESCE(SUM(input_chars), 0) AS input_chars,
                    COALESCE(SUM(output_chars), 0) AS output_chars,
                    COALESCE(SUM(input_tokens_est), 0) AS input_tokens_est,
                    COALESCE(SUM(output_tokens_est), 0) AS output_tokens_est
                FROM message_metrics
            """)
            row = cursor.fetchone()
            return dict(row) if row else {}

    def get_recent_tasks(self, limit: int = 20) -> List[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT
                    t.id, t.mission_id, t.title, t.assigned_ant, t.task_type,
                    t.status, t.result_summary, t.result_chars, t.estimated_tokens,
                    t.started_at, t.finished_at, t.elapsed_seconds,
                    m.goal AS mission_goal
                FROM tasks t
                LEFT JOIN missions m ON t.mission_id = m.id
                ORDER BY COALESCE(t.finished_at, t.started_at, m.saved_at, m.created_at) DESC
                LIMIT ?
            """, (limit,))
            return [dict(row) for row in cursor.fetchall()]

    def summarize_task_metrics(self) -> Dict[str, Any]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT
                    COUNT(*) AS task_count,
                    COALESCE(AVG(elapsed_seconds), 0) AS avg_elapsed_seconds,
                    COALESCE(MAX(elapsed_seconds), 0) AS max_elapsed_seconds,
                    COALESCE(SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END), 0) AS failed_count,
                    COALESCE(SUM(CASE WHEN status = 'skipped' THEN 1 ELSE 0 END), 0) AS skipped_count
                FROM tasks
            """)
            row = cursor.fetchone()
            return dict(row) if row else {}

    def get_patch_proposal(self, patch_id: str) -> Optional[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT pp.*, ps.summary AS patch_set_summary, m.goal AS mission_goal
                FROM patch_proposals pp
                LEFT JOIN patch_sets ps ON pp.patch_set_id = ps.id
                LEFT JOIN missions m ON pp.mission_id = m.id
                WHERE pp.id = ?
            """, (patch_id,))
            row = cursor.fetchone()
            return dict(row) if row else None

    def list_patch_proposals(self, status: Optional[PatchStatus] = None, limit: int = PATCH_LIST_LIMIT_DEFAULT) -> List[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            base_sql = """
                SELECT pp.id, pp.patch_set_id, pp.mission_id, pp.task_id,
                       pp.file_path, pp.change_type, pp.reason, pp.risk,
                       pp.requires_approval, pp.status, pp.created_at,
                       pp.applied_at, pp.backup_path, pp.last_error,
                       ps.summary AS patch_set_summary
                FROM patch_proposals pp
                LEFT JOIN patch_sets ps ON pp.patch_set_id = ps.id
            """
            if status is None:
                cursor.execute(base_sql + " ORDER BY pp.created_at DESC LIMIT ?", (limit,))
            else:
                cursor.execute(base_sql + " WHERE pp.status = ? ORDER BY pp.created_at DESC LIMIT ?", (status.value, limit))
            return [dict(row) for row in cursor.fetchall()]

    def update_patch_status(self, patch_id: str, status: PatchStatus, applied_at: Optional[str] = None,
                            backup_path: Optional[str] = None, last_error: Optional[str] = None):
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                UPDATE patch_proposals
                SET status = ?, applied_at = COALESCE(?, applied_at),
                    backup_path = COALESCE(?, backup_path), last_error = ?
                WHERE id = ?
            """, (status.value, applied_at, backup_path, last_error, patch_id))
            conn.commit()

    def save_approval_request(self, approval: ApprovalRequest):
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT OR REPLACE INTO approval_requests (
                    id, mission_id, task_id, action_type, target_id, title,
                    description, status, requested_by, decision_note,
                    metadata_json, created_at, decided_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """, (
                approval.id, approval.mission_id, approval.task_id, approval.action_type.value,
                approval.target_id, approval.title, approval.description, approval.status.value,
                approval.requested_by, approval.decision_note, safe_json_dumps(approval.metadata),
                approval.created_at.isoformat(), approval.decided_at.isoformat() if approval.decided_at else None,
            ))
            conn.commit()

    def get_approval_request(self, approval_id: str) -> Optional[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM approval_requests WHERE id = ?", (approval_id,))
            row = cursor.fetchone()
            return dict(row) if row else None

    def get_approval_for_target(self, target_id: str, action_type: ApprovalActionType = ApprovalActionType.PATCH_PROPOSAL) -> Optional[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT * FROM approval_requests
                WHERE target_id = ? AND action_type = ?
                ORDER BY created_at DESC LIMIT 1
            """, (target_id, action_type.value))
            row = cursor.fetchone()
            return dict(row) if row else None

    def list_approval_requests(self, status: Optional[ApprovalStatus] = ApprovalStatus.PENDING, limit: int = 20) -> List[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            if status is None:
                cursor.execute("SELECT * FROM approval_requests ORDER BY created_at DESC LIMIT ?", (limit,))
            else:
                cursor.execute("SELECT * FROM approval_requests WHERE status = ? ORDER BY created_at DESC LIMIT ?", (status.value, limit))
            return [dict(row) for row in cursor.fetchall()]

    def count_pending_approvals(self) -> int:
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT COUNT(*) FROM approval_requests WHERE status = ?", (ApprovalStatus.PENDING.value,))
            return int(cursor.fetchone()[0])

    def update_approval_status(self, approval_id: str, new_status: ApprovalStatus, decision_note: Optional[str] = None) -> Optional[Dict[str, Any]]:
        if not self.get_approval_request(approval_id):
            return None
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                UPDATE approval_requests
                SET status = ?, decision_note = ?, decided_at = ?
                WHERE id = ?
            """, (new_status.value, decision_note, now_utc().isoformat(), approval_id))
            conn.commit()
        return self.get_approval_request(approval_id)

    def log_event(self, mission_id: str, event_type: str, message: str,
                  task_id: Optional[str] = None, ant_name: Optional[str] = None,
                  metadata: Optional[Dict[str, Any]] = None) -> Event:
        event = Event(
            mission_id=mission_id, task_id=task_id, ant_name=ant_name,
            event_type=event_type, message=message, metadata=metadata or {},
        )
        with self._connect() as conn:
            cursor = conn.cursor()
            cursor.execute("""
                INSERT INTO events (
                    id, mission_id, task_id, ant_name, event_type,
                    message, metadata_json, created_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """, (
                event.id, event.mission_id, event.task_id, event.ant_name,
                event.event_type, event.message, safe_json_dumps(event.metadata),
                event.created_at.isoformat(),
            ))
            conn.commit()
        return event

    def get_recent_events(
        self,
        limit: int = EVENT_LIST_LIMIT_DEFAULT,
        event_type: Optional[str] = None,
        mission_id: Optional[str] = None,
    ) -> List[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            query = """
                SELECT id, mission_id, task_id, ant_name, event_type, message,
                       metadata_json, created_at
                FROM events
            """
            conditions = []
            params: List[Any] = []
            if event_type:
                conditions.append("event_type = ?")
                params.append(event_type)
            if mission_id:
                conditions.append("mission_id = ?")
                params.append(mission_id)
            if conditions:
                query += " WHERE " + " AND ".join(conditions)
            query += " ORDER BY created_at DESC LIMIT ?"
            params.append(limit)
            cursor.execute(query, tuple(params))
            return [dict(row) for row in cursor.fetchall()]

    def summarize_events(self) -> Dict[str, Any]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT
                    COUNT(*) AS event_count,
                    COALESCE(SUM(CASE WHEN event_type IN (
                        'task_failed', 'tool_failed', 'patch_apply_failed',
                        'patch_proposal_parse_failed', 'mission_timeout',
                        'task_timeout', 'model_call_failed'
                    ) THEN 1 ELSE 0 END), 0) AS failure_event_count,
                    COALESCE(SUM(CASE WHEN event_type = 'task_completed' THEN 1 ELSE 0 END), 0) AS task_completed_count,
                    COALESCE(SUM(CASE WHEN event_type = 'model_call_completed' THEN 1 ELSE 0 END), 0) AS model_call_count,
                    MAX(created_at) AS last_event_at
                FROM events
            """)
            summary = dict(cursor.fetchone() or {})
            cursor.execute("""
                SELECT event_type, COUNT(*) AS count
                FROM events
                GROUP BY event_type
                ORDER BY count DESC, event_type ASC
                LIMIT 12
            """)
            summary["top_event_types"] = [dict(row) for row in cursor.fetchall()]
            return summary

    def get_recent_failure_events(self, limit: int = DIAGNOSTIC_EVENT_LIMIT) -> List[Dict[str, Any]]:
        placeholders = ",".join("?" for _ in FAILURE_EVENT_TYPES)
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute(f"""
                SELECT id, mission_id, task_id, ant_name, event_type, message,
                       metadata_json, created_at
                FROM events
                WHERE event_type IN ({placeholders})
                ORDER BY created_at DESC
                LIMIT ?
            """, tuple(FAILURE_EVENT_TYPES) + (limit,))
            return [dict(row) for row in cursor.fetchall()]

    def update_pheromone_trail(self, trail_key: str, trail_type: str, success: bool,
                               strength_delta: float, metadata: Optional[Dict[str, Any]] = None):
        metadata = metadata or {}
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("SELECT * FROM pheromone_trails WHERE trail_key = ?", (trail_key,))
            existing = cursor.fetchone()
            if existing:
                new_strength = max(0.0, min(1.0, round(float(existing["strength"]) + strength_delta, 4)))
                success_count = int(existing["success_count"]) + (1 if success else 0)
                failure_count = int(existing["failure_count"]) + (0 if success else 1)
                try:
                    old_metadata = json.loads(existing["metadata_json"] or "{}")
                except Exception:
                    old_metadata = {}
                merged_metadata = {**old_metadata, **metadata}
                cursor.execute("""
                    UPDATE pheromone_trails
                    SET strength = ?, success_count = ?, failure_count = ?,
                        last_updated = ?, metadata_json = ?
                    WHERE trail_key = ?
                """, (new_strength, success_count, failure_count, now_utc().isoformat(), safe_json_dumps(merged_metadata), trail_key))
            else:
                initial_strength = max(0.0, min(1.0, round(0.5 + strength_delta, 4)))
                cursor.execute("""
                    INSERT INTO pheromone_trails (
                        id, trail_key, trail_type, strength,
                        success_count, failure_count, last_updated, metadata_json
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """, (str(uuid4()), trail_key, trail_type, initial_strength, 1 if success else 0, 0 if success else 1, now_utc().isoformat(), safe_json_dumps(metadata)))
            conn.commit()

    def update_mission_pheromones(self, mission: Mission):
        success = mission.status in {MissionStatus.COMPLETE, MissionStatus.PARTIAL}
        score = mission.success_score or 0.0
        if mission.status == MissionStatus.COMPLETE:
            delta = 0.05 + (score * 0.05)
        elif mission.status == MissionStatus.PARTIAL:
            delta = 0.01 + (score * 0.02)
        else:
            delta = -0.08

        ant_path = [task.assigned_ant for task in mission.tasks]
        task_type_path = [task.task_type for task in mission.tasks]

        self.update_pheromone_trail(
            trail_key="planner_pattern:" + "_".join(ant_path), trail_type="planner_pattern",
            success=success, strength_delta=delta,
            metadata={"mission_id": mission.id, "goal": mission.goal, "score": score, "ant_path": ant_path, "mission_status": mission.status.value},
        )

        self.update_pheromone_trail(
            trail_key="task_pattern:" + "_".join(task_type_path), trail_type="task_pattern",
            success=success, strength_delta=delta,
            metadata={"mission_id": mission.id, "goal": mission.goal, "score": score, "task_type_path": task_type_path, "mission_status": mission.status.value},
        )

        for task in mission.tasks:
            task_success = task.status == TaskStatus.COMPLETE
            if task.status == TaskStatus.SKIPPED:
                task_delta = -0.01
            elif task_success and success:
                task_delta = 0.03
            else:
                task_delta = -0.04
            self.update_pheromone_trail(
                trail_key=f"ant:{task.assigned_ant}", trail_type="ant", success=task_success,
                strength_delta=task_delta,
                metadata={"last_mission_id": mission.id, "last_task_id": task.id, "task_type": task.task_type, "task_status": task.status.value},
            )
            self.update_pheromone_trail(
                trail_key=f"task_type:{task.task_type}", trail_type="task_type", success=task_success,
                strength_delta=task_delta,
                metadata={"last_mission_id": mission.id, "last_task_id": task.id, "assigned_ant": task.assigned_ant, "task_status": task.status.value},
            )

    def get_recent_missions(self, limit: int = 5) -> List[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT id, goal, status, user_result, debug_result, final_result,
                       best_output_task_id, success_score, created_at, saved_at
                FROM missions
                ORDER BY saved_at DESC
                LIMIT ?
            """, (limit,))
            return [dict(row) for row in cursor.fetchall()]

    def search_relevant_missions(self, goal: str, limit: int = 5) -> List[Dict[str, Any]]:
        query_keywords = extract_keywords(goal)
        if not query_keywords:
            return []

        if self.fts_available:
            fts_query = " OR ".join(sorted(query_keywords)[:8])
            try:
                with self._connect() as conn:
                    conn.row_factory = sqlite3.Row
                    cursor = conn.cursor()
                    cursor.execute("""
                        SELECT m.id, m.goal, m.status, m.user_result, m.final_result,
                               m.success_score, m.saved_at
                        FROM missions_fts f
                        JOIN missions m ON m.id = f.id
                        WHERE missions_fts MATCH ?
                        ORDER BY bm25(missions_fts)
                        LIMIT ?
                    """, (fts_query, limit))
                    return [dict(row) for row in cursor.fetchall()]
            except sqlite3.OperationalError:
                self.fts_available = False

        return self._keyword_search_relevant_missions(goal, limit)

    def _keyword_search_relevant_missions(self, goal: str, limit: int = 5) -> List[Dict[str, Any]]:
        query_keywords = extract_keywords(goal)
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT id, goal, status, user_result, final_result, success_score, saved_at
                FROM missions
                ORDER BY saved_at DESC
                LIMIT 50
            """)
            rows = [dict(row) for row in cursor.fetchall()]
        scored: List[Tuple[int, Dict[str, Any]]] = []
        for row in rows:
            memory_text = f"{row.get('goal') or ''} {row.get('user_result') or ''} {row.get('final_result') or ''}"
            overlap = query_keywords.intersection(extract_keywords(memory_text))
            if overlap:
                scored.append((len(overlap), row))
        scored.sort(key=lambda item: item[0], reverse=True)
        return [row for _, row in scored[:limit]]

    def get_top_pheromone_trails(self, limit: int = 10) -> List[Dict[str, Any]]:
        with self._connect() as conn:
            conn.row_factory = sqlite3.Row
            cursor = conn.cursor()
            cursor.execute("""
                SELECT trail_key, trail_type, strength, success_count, failure_count, last_updated
                FROM pheromone_trails
                ORDER BY strength DESC, success_count DESC
                LIMIT ?
            """, (limit,))
            return [dict(row) for row in cursor.fetchall()]

    def format_recent_memory(self, limit: int = 3, max_result_chars: int = 300) -> str:
        recent_missions = self.get_recent_missions(limit=limit)
        if not recent_missions:
            return "No recent mission memory found."
        blocks = []
        for mission in recent_missions:
            result_summary = mission.get("user_result") or mission.get("final_result") or ""
            blocks.append(
                f"Previous Goal: {mission.get('goal')}\n"
                f"Previous Status: {mission.get('status')}\n"
                f"Previous Pheromone Score: {mission.get('success_score')}\n"
                f"Previous Result Summary:\n{truncate_text(result_summary, max_result_chars, suffix='...[memory result truncated]')}\n"
            )
        return "\n---\n".join(blocks)

    def format_relevant_memory(self, goal: str, limit: int = 5, max_result_chars: int = 300) -> str:
        relevant_missions = self.search_relevant_missions(goal=goal, limit=limit)
        if not relevant_missions:
            return "No relevant mission memory found."
        blocks = []
        for mission in relevant_missions:
            result_summary = mission.get("user_result") or mission.get("final_result") or ""
            blocks.append(
                f"Relevant Goal: {mission.get('goal')}\n"
                f"Relevant Status: {mission.get('status')}\n"
                f"Relevant Pheromone Score: {mission.get('success_score')}\n"
                f"Relevant Result Summary:\n{truncate_text(result_summary, max_result_chars, suffix='...[relevant memory truncated]')}\n"
            )
        return "\n---\n".join(blocks)

    def format_pheromone_context(self, limit: int = 8) -> str:
        trails = self.get_top_pheromone_trails(limit=limit)
        if not trails:
            return "No pheromone trail memory found."
        return "\n".join(
            f"{trail['trail_key']} | type={trail['trail_type']} | strength={trail['strength']} | "
            f"success={trail['success_count']} | failure={trail['failure_count']}"
            for trail in trails
        )


# ============================================================
#  TOOL SYSTEM
# ============================================================

class BaseTool(ABC):
    name: str = "base_tool"
    description: str = "Base tool"

    @abstractmethod
    def run(self, **kwargs) -> ToolResult:
        pass


class ToolRegistry:
    def __init__(self, memory: SQLiteMemory):
        self.tools: Dict[str, BaseTool] = {}
        self.memory = memory

    def register(self, tool: BaseTool):
        self.tools[tool.name] = tool

    def run_tool(self, name: str, mission_id: Optional[str] = None, task_id: Optional[str] = None,
                 ant_name: Optional[str] = None, **kwargs) -> ToolResult:
        if mission_id:
            self.memory.log_event(
                mission_id=mission_id, task_id=task_id, ant_name=ant_name,
                event_type="tool_called", message=f"Tool called: {name}",
                metadata={"tool_name": name, "arguments": self._safe_metadata(kwargs)},
            )
        tool = self.tools.get(name)
        if not tool:
            result = ToolResult(tool_name=name, success=False, output="", error=f"Tool not found or not registered: {name}")
            if mission_id:
                self._log_tool_result(mission_id, task_id, ant_name, result)
            return result
        try:
            tool_kwargs = dict(kwargs)
            tool_kwargs.setdefault("mission_id", mission_id)
            tool_kwargs.setdefault("task_id", task_id)
            tool_kwargs.setdefault("ant_name", ant_name)
            result = tool.run(**tool_kwargs)
        except Exception as error:
            result = ToolResult(tool_name=name, success=False, output="", error=f"Tool execution failed: {error}")
        if mission_id:
            self._log_tool_result(mission_id, task_id, ant_name, result)
            self.memory.update_pheromone_trail(
                trail_key=f"tool:{name}", trail_type="tool", success=result.success,
                strength_delta=0.02 if result.success else -0.04,
                metadata={"mission_id": mission_id, "task_id": task_id, "ant_name": ant_name},
            )
        return result

    def _log_tool_result(self, mission_id: str, task_id: Optional[str], ant_name: Optional[str], result: ToolResult):
        self.memory.log_event(
            mission_id=mission_id, task_id=task_id, ant_name=ant_name,
            event_type="tool_completed" if result.success else "tool_failed",
            message=f"Tool {'completed' if result.success else 'failed'}: {result.tool_name}",
            metadata={"tool_name": result.tool_name, "success": result.success, "error": result.error, "output_preview": truncate_text(result.output, 500)},
        )

    def _safe_metadata(self, metadata: Dict[str, Any]) -> Dict[str, Any]:
        safe: Dict[str, Any] = {}
        for key, value in metadata.items():
            if isinstance(value, (str, int, float, bool)) or value is None:
                safe[key] = value
            else:
                safe[key] = str(value)
        return safe

    def describe_tools(self) -> str:
        if not self.tools:
            return "No tools registered."
        return "\n".join(f"- {name}: {tool.description}" for name, tool in self.tools.items())


class WorkspacePathGuard:
    def __init__(self, root: str = ALLOWED_WORKSPACE_ROOT):
        root_path = Path(root)
        self.root = root_path.resolve() if root_path.is_absolute() else (SCRIPT_DIR / root_path).resolve()

    def resolve_safe_path(self, requested_path: str) -> Path:
        requested = Path(requested_path)
        if not requested.is_absolute():
            requested = self.root / requested
        resolved = requested.resolve()
        try:
            resolved.relative_to(self.root)
        except ValueError:
            raise ValueError(f"Access denied. Path is outside allowed workspace root: {self.root}")
        return resolved

    def is_blocked_path(self, path: Path) -> bool:
        return bool({part.lower() for part in path.parts}.intersection(BLOCKED_PATH_PARTS))


class SystemInfoTool(BaseTool):
    name = "system_info"
    description = "Read-only tool that returns basic OS, Python, and workspace information."

    def run(self, **kwargs) -> ToolResult:
        info = {
            "os": platform.system(),
            "os_release": platform.release(),
            "os_version": platform.version(),
            "machine": platform.machine(),
            "python_version": sys.version,
            "current_working_directory": os.getcwd(),
            "script_directory": str(SCRIPT_DIR),
            "allowed_workspace_root": str(resolve_workspace_root()),
            "file_tools_enabled": ENABLE_FILE_TOOLS,
            "shell_tool_enabled": ENABLE_SHELL_TOOL,
            "patch_application_enabled": ENABLE_PATCH_APPLICATION,
            "file_writing_enabled": ENABLE_FILE_WRITING,
            "parallel_execution_enabled": ENABLE_PARALLEL_EXECUTION,
            "max_parallel_workers": MAX_PARALLEL_WORKERS,
            "fts_memory_enabled": ENABLE_FTS_MEMORY,
        }
        return ToolResult(tool_name=self.name, success=True, output=json.dumps(info, indent=2))


class DirectoryListTool(BaseTool):
    name = "list_directory"
    description = "Read-only tool that lists files and folders inside the allowed workspace."

    def __init__(self, path_guard: WorkspacePathGuard):
        self.path_guard = path_guard

    def run(self, **kwargs) -> ToolResult:
        if not ENABLE_FILE_TOOLS:
            return ToolResult(tool_name=self.name, success=False, output="", error="File tools are disabled by config.")
        requested_path = kwargs.get("path", ".")
        safe_path = self.path_guard.resolve_safe_path(str(requested_path))
        if self.path_guard.is_blocked_path(safe_path):
            return ToolResult(tool_name=self.name, success=False, output="", error="Refusing to list blocked internal/system path.")
        if not safe_path.exists():
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Directory does not exist: {safe_path}")
        if not safe_path.is_dir():
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Path is not a directory: {safe_path}")
        items = []
        for index, child in enumerate(sorted(safe_path.iterdir(), key=lambda p: p.name.lower())):
            if index >= MAX_DIRECTORY_ITEMS:
                items.append(f"...[truncated after {MAX_DIRECTORY_ITEMS} items]")
                break
            if self.path_guard.is_blocked_path(child):
                continue
            item_type = "DIR " if child.is_dir() else "FILE"
            items.append(f"{item_type}  {child.name}")
        output = "\n".join(items) if items else "(directory is empty or all items are blocked)"
        return ToolResult(tool_name=self.name, success=True, output=output)


class ReadTextFileTool(BaseTool):
    name = "read_text_file"
    description = "Read-only tool that reads text files inside the allowed workspace with a character limit."
    TEXT_SUFFIXES = PATCH_ALLOWED_SUFFIXES

    def __init__(self, path_guard: WorkspacePathGuard):
        self.path_guard = path_guard

    def run(self, **kwargs) -> ToolResult:
        if not ENABLE_FILE_TOOLS:
            return ToolResult(tool_name=self.name, success=False, output="", error="File tools are disabled by config.")
        requested_path = kwargs.get("path")
        if not requested_path:
            return ToolResult(tool_name=self.name, success=False, output="", error="Missing required argument: path")
        safe_path = self.path_guard.resolve_safe_path(str(requested_path))
        if self.path_guard.is_blocked_path(safe_path):
            return ToolResult(tool_name=self.name, success=False, output="", error="Refusing to read from blocked internal/system path.")
        if safe_path.suffix.lower() in BLOCKED_FILE_SUFFIXES:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Refusing to read blocked file type: {safe_path.suffix}")
        if not safe_path.exists():
            return ToolResult(tool_name=self.name, success=False, output="", error=f"File does not exist: {safe_path}")
        if not safe_path.is_file():
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Path is not a file: {safe_path}")
        if safe_path.suffix.lower() not in self.TEXT_SUFFIXES:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Refusing to read unsupported file type: {safe_path.suffix}")
        try:
            content = safe_path.read_text(encoding="utf-8", errors="replace")
        except Exception as error:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Could not read file: {error}")
        content = truncate_text(content, MAX_FILE_READ_CHARS, suffix=f"...[file truncated after {MAX_FILE_READ_CHARS} characters]")
        return ToolResult(tool_name=self.name, success=True, output=content)


class ShellCommandTool(BaseTool):
    name = "shell_command"
    description = "Optional minimal shell command tool. Disabled by default. High risk."
    SAFE_COMMANDS = {"dir", "ls", "pwd", "echo"}

    def run(self, **kwargs) -> ToolResult:
        if not ENABLE_SHELL_TOOL:
            return ToolResult(tool_name=self.name, success=False, output="", error="Shell tool is disabled by config.")
        command = str(kwargs.get("command", "")).strip()
        if not command:
            return ToolResult(tool_name=self.name, success=False, output="", error="Missing required argument: command")
        try:
            args = shlex.split(command)
        except ValueError as error:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Could not parse command safely: {error}")
        if not args:
            return ToolResult(tool_name=self.name, success=False, output="", error="Empty command after parsing.")
        base_command = args[0].lower()
        if base_command not in self.SAFE_COMMANDS:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Command is not allowlisted: {base_command}")
        try:
            completed = subprocess.run(args, shell=False, capture_output=True, text=True, timeout=30, cwd=str(resolve_workspace_root()))
            return ToolResult(tool_name=self.name, success=completed.returncode == 0, output=completed.stdout.strip(), error=completed.stderr.strip() or None)
        except Exception as error:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Shell command failed: {error}")


class WebSearchTool(BaseTool):
    name = "web_search"
    description = "Read-only web search tool for current/public information. Disabled unless ENABLE_WEB_SEARCH=True."

    def run(self, **kwargs) -> ToolResult:
        if not ENABLE_WEB_SEARCH:
            return ToolResult(self.name, False, "", "Web search is disabled by config. Set ENABLE_WEB_SEARCH=True to enable read-only external research.")

        query = str(kwargs.get("query", "")).strip()
        max_results = int(kwargs.get("max_results", MAX_WEB_RESULTS) or MAX_WEB_RESULTS)
        max_results = max(1, min(max_results, MAX_WEB_RESULTS))

        if not query:
            return ToolResult(self.name, False, "", "Missing required argument: query")

        try:
            return self._duckduckgo_html_search(query=query, max_results=max_results)
        except Exception as error:
            return ToolResult(self.name, False, "", f"Web search failed: {error}")

    def _duckduckgo_html_search(self, query: str, max_results: int) -> ToolResult:
        url = "https://duckduckgo.com/html/"
        headers = {"User-Agent": "ANTHILL-Core/1.3 read-only research"}
        response = requests.get(url, params={"q": query}, headers=headers, timeout=WEB_SEARCH_TIMEOUT_SECONDS)
        response.raise_for_status()
        html = response.text

        results: List[Dict[str, str]] = []
        # DuckDuckGo HTML uses result__a links. This parser is intentionally lightweight
        # so ANTHILL remains single-file with no bs4 dependency.
        pattern = re.compile(r'<a[^>]+class="result__a"[^>]+href="([^"]+)"[^>]*>(.*?)</a>', re.IGNORECASE | re.DOTALL)
        for match in pattern.finditer(html):
            raw_url = match.group(1)
            title = strip_html_tags(match.group(2))
            if not title or not raw_url:
                continue
            # v1.3.2: decode redirects at the tool boundary so ToolResult.output,
            # source records, quality scoring, and user-visible URLs all agree.
            clean_url = decode_search_url(raw_url)
            snippet = ""
            results.append({"title": title, "url": clean_url, "snippet": snippet, "source": WEB_SEARCH_PROVIDER})
            if len(results) >= max_results:
                break

        if not results:
            # Fallback: store plain-page text preview so the researcher still knows the query ran.
            preview = truncate_text(strip_html_tags(html), 1000, suffix="...[search page truncated]")
            return ToolResult(self.name, True, json.dumps({"query": query, "results": [], "preview": preview}, indent=2))

        return ToolResult(self.name, True, json.dumps({"query": query, "results": results}, indent=2))


class ApplyPatchTool(BaseTool):
    name = "apply_patch"
    description = "Approval-gated tool that applies safe ADD or MODIFY patch proposals with backups."

    def __init__(self, path_guard: WorkspacePathGuard):
        self.path_guard = path_guard

    def run(self, **kwargs) -> ToolResult:
        if not ENABLE_PATCH_APPLICATION:
            return ToolResult(tool_name=self.name, success=False, output="", error="Patch application is disabled by config.")
        if not ENABLE_FILE_WRITING:
            return ToolResult(tool_name=self.name, success=False, output="", error="File writing is disabled by config.")
        patch = kwargs.get("patch")
        if not isinstance(patch, dict):
            return ToolResult(tool_name=self.name, success=False, output="", error="Missing required dict argument: patch")
        change_type = str(patch.get("change_type", "")).lower().strip()
        file_path = str(patch.get("file_path", "")).strip()
        old_content = patch.get("old_content")
        new_content = patch.get("new_content")
        try:
            validate_safe_patch_path(file_path)
            safe_path = self.path_guard.resolve_safe_path(file_path)
        except Exception as error:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Unsafe patch path: {error}")
        if self.path_guard.is_blocked_path(safe_path):
            return ToolResult(tool_name=self.name, success=False, output="", error="Refusing to patch blocked internal/system path.")
        if change_type not in {PatchChangeType.ADD.value, PatchChangeType.MODIFY.value}:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"v1.3 only supports add and modify patches. Refusing change_type: {change_type}")
        if not isinstance(new_content, str) or not new_content:
            return ToolResult(tool_name=self.name, success=False, output="", error="Patch new_content is required and must be non-empty.")
        try:
            if change_type == PatchChangeType.ADD.value:
                return self._apply_add(safe_path, new_content)
            if change_type == PatchChangeType.MODIFY.value:
                if not isinstance(old_content, str) or not old_content:
                    return ToolResult(tool_name=self.name, success=False, output="", error="MODIFY patches require old_content for exact replacement.")
                return self._apply_modify(safe_path, old_content, new_content)
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Unsupported change_type: {change_type}")
        except Exception as error:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"Patch application failed: {error}")

    def _backup_file(self, path: Path) -> Optional[Path]:
        if not path.exists():
            return None
        backup_root = (SCRIPT_DIR / BACKUP_DIR).resolve()
        backup_root.mkdir(parents=True, exist_ok=True)
        safe_name = str(path.relative_to(resolve_workspace_root())).replace("\\", "__").replace("/", "__")
        backup_path = backup_root / f"{safe_name}.{timestamp_id()}.bak"
        shutil.copy2(path, backup_path)
        return backup_path

    def _apply_add(self, safe_path: Path, new_content: str) -> ToolResult:
        if safe_path.exists():
            return ToolResult(tool_name=self.name, success=False, output="", error=f"ADD refused because file already exists: {safe_path}")
        safe_path.parent.mkdir(parents=True, exist_ok=True)
        safe_path.write_text(new_content, encoding="utf-8")
        return ToolResult(tool_name=self.name, success=True, output=json.dumps({"action": "add", "file_path": str(safe_path), "backup_path": None}, indent=2))

    def _apply_modify(self, safe_path: Path, old_content: str, new_content: str) -> ToolResult:
        if not safe_path.exists():
            return ToolResult(tool_name=self.name, success=False, output="", error=f"MODIFY refused because file does not exist: {safe_path}")
        if not safe_path.is_file():
            return ToolResult(tool_name=self.name, success=False, output="", error=f"MODIFY refused because path is not a file: {safe_path}")
        current_content = safe_path.read_text(encoding="utf-8", errors="replace")
        occurrences = current_content.count(old_content)
        if occurrences == 0:
            return ToolResult(tool_name=self.name, success=False, output="", error="MODIFY refused because old_content was not found exactly in the target file.")
        if occurrences > 1:
            return ToolResult(tool_name=self.name, success=False, output="", error=f"MODIFY refused because old_content appears {occurrences} times. Patch must be unambiguous.")
        backup_path = self._backup_file(safe_path)
        updated_content = current_content.replace(old_content, new_content, 1)
        safe_path.write_text(updated_content, encoding="utf-8")
        return ToolResult(tool_name=self.name, success=True, output=json.dumps({"action": "modify", "file_path": str(safe_path), "backup_path": str(backup_path) if backup_path else None}, indent=2))


# ============================================================
#  PLANNER
# ============================================================

class Planner:
    ALLOWED_ANTS = {"researcher", "web", "file", "coder", "builder", "verifier"}

    def __init__(self, use_ollama: bool = USE_OLLAMA, model_router: Optional[ModelRouter] = None):
        self.use_ollama = use_ollama
        self.model_router = model_router

    def create_tasks(self, goal: str, memory_context: str = "", tool_context: str = "", pheromone_context: str = "") -> List[Task]:
        if not self.use_ollama or self.model_router is None:
            return self._fallback_tasks(goal)
        prompt = f"""
ANTHILL v1.4 | role: planner | timestamp: {now_utc().isoformat()} | mission: {truncate_text(goal, 180)}
You are concise. Do not explain your reasoning unless asked.

You are the Planner inside ANTHILL, a local swarm-intelligence AI harness.

Available ants:
- researcher: summarizes local memory, tool context, and mission-relevant internal context.
- web: performs read-only external research when the mission requires current/public information.
- file: inspects workspace files read-only. Use only for file/code/repo/folder missions.
- coder: proposes structured JSON patches only.
- builder: creates the final response from prior ant outputs.
- verifier: verifies result quality and safety.

Available tools:
{tool_context}

Memory:
{memory_context}

Pheromone trail summary. Prefer high-strength matching patterns, but do not force them if the mission does not fit:
{pheromone_context}

Mission goal:
{goal}

Rules:
- Return ONLY valid JSON.
Do not wrap JSON in markdown code fences.
- Create between {MIN_DYNAMIC_TASKS} and {MAX_DYNAMIC_TASKS} tasks.
- assigned_ant must be one of: researcher, web, file, coder, builder, verifier.
- Keep each task description under 100 words.
- Skip the file ant unless file/code/repo/folder/path keywords appear in the goal.
- Use web only when the mission needs current, public, external, version, docs, price, news, or online information.
- Use file/coder for code, scripts, patches, folders, repos, bugs, or refactors.
- Do not ask ants to write files.
- Patch application is user-triggered later through /apply after approval.
- Final task should usually be verifier.
- depends_on should usually be [] because ANTHILL auto-wires safe dependencies in v1.3.

Required JSON:
{{
  "tasks": [
    {{
      "title": "Short title",
      "description": "Clear task description under 100 words",
      "assigned_ant": "researcher",
      "task_type": "research",
      "depends_on": []
    }}
  ]
}}
"""
        response = self.model_router.generate("planner", prompt, ant_name="planner")
        if response.startswith("ERROR:"):
            print(f"[red]Planner failed to use Ollama:[/red] {response}")
            print("[yellow]Using fallback static task plan.[/yellow]")
            return self._fallback_tasks(goal)
        try:
            parsed = extract_json_object(response)
            tasks = self._tasks_from_json(parsed, goal)
            if not tasks:
                print("[yellow]Dynamic planner returned no valid task plan. Using fallback plan.[/yellow]")
                return self._fallback_tasks(goal)
            print("[bold blue]Dynamic planner created mission-specific tasks.[/bold blue]")
            return tasks
        except Exception as error:
            print(f"[yellow]Dynamic planner parse failed:[/yellow] {error}")
            print("[yellow]Using fallback static task plan.[/yellow]")
            return self._fallback_tasks(goal)

    def _tasks_from_json(self, parsed: Dict[str, Any], goal: str) -> List[Task]:
        raw_tasks = parsed.get("tasks")
        if raw_tasks is None or not isinstance(raw_tasks, list):
            return []
        tasks: List[Task] = []
        dropped_tasks = 0
        for item in raw_tasks[:MAX_DYNAMIC_TASKS]:
            if not isinstance(item, dict):
                dropped_tasks += 1
                continue
            title = str(item.get("title", "")).strip() or "Task"
            description = str(item.get("description", "")).strip() or f"Handle part of the mission: {goal}"
            assigned_ant = str(item.get("assigned_ant", "")).strip().lower()
            if assigned_ant not in self.ALLOWED_ANTS:
                dropped_tasks += 1
                continue
            task_type = str(item.get("task_type", "")).strip().lower() or infer_task_type(assigned_ant, title, description)
            depends_on = item.get("depends_on", [])
            if not isinstance(depends_on, list):
                depends_on = []
            tasks.append(Task(title=title, description=description, assigned_ant=assigned_ant, task_type=task_type, depends_on=[str(x) for x in depends_on]))
        if dropped_tasks:
            print(f"[yellow]Planner dropped {dropped_tasks} invalid task(s).[/yellow]")
        if len(tasks) < MIN_DYNAMIC_TASKS:
            return []
        if not any(task.assigned_ant == "verifier" for task in tasks):
            tasks.append(Task(title="Verify mission output", description=f"Check the final result for accuracy, completeness, and usefulness: {goal}", assigned_ant="verifier", task_type="verification"))
        return tasks[:MAX_DYNAMIC_TASKS]

    def _fallback_tasks(self, goal: str) -> List[Task]:
        lowered = goal.lower()
        code_keywords = ["code", "script", "python", "bug", "debug", "review", "refactor", "function", "class", "repo", "repository", "file", "folder", "directory", "patch", "modify", "change"]
        if ENABLE_WEB_SEARCH and should_use_web_search(goal):
            return [
                Task(title="Frame research need", description=f"Identify what current/public information is needed for: {goal}", assigned_ant="researcher", task_type="research"),
                Task(title="External web research", description=f"Run read-only web research and save source records for: {goal}", assigned_ant="web", task_type="external_research"),
                Task(title="Build sourced response", description=f"Create a concise answer using internal context and saved source summaries: {goal}", assigned_ant="builder", task_type="build_answer"),
                Task(title="Verify sourced result", description=f"Check that the answer addresses the question and notes source limitations: {goal}", assigned_ant="verifier", task_type="verification"),
            ]
        if any(keyword in lowered for keyword in code_keywords):
            return [
                Task(title="Research mission", description=f"Understand the goal and frame the code/project inspection need: {goal}", assigned_ant="researcher", task_type="research"),
                Task(title="Inspect workspace files", description=f"List relevant workspace files and read safe text files if useful: {goal}", assigned_ant="file", task_type="file_inspection"),
                Task(title="Create structured patch proposal", description=f"Analyze available code/file context and propose structured patches as JSON only: {goal}", assigned_ant="coder", task_type="patch_proposal"),
                Task(title="Build final response", description=f"Create a practical answer or implementation plan from the prior findings: {goal}", assigned_ant="builder", task_type="build_answer"),
                Task(title="Verify result", description=f"Check the result for accuracy, usefulness, missing steps, and risk: {goal}", assigned_ant="verifier", task_type="verification"),
            ]
        return [
            Task(title="Research mission", description=f"Understand the goal and gather useful context: {goal}", assigned_ant="researcher", task_type="research"),
            Task(title="Build response", description=f"Create a practical answer or action plan for: {goal}", assigned_ant="builder", task_type="build_answer"),
            Task(title="Verify result", description=f"Check the result for accuracy, usefulness, and missing steps: {goal}", assigned_ant="verifier", task_type="verification"),
        ]


# ============================================================
#  PHEROMONE ENGINE + PATCH PARSER
# ============================================================

class PheromoneEngine:
    QUALITY_BONUS_THRESHOLD = 1500

    def score_mission(self, mission: Mission) -> float:
        total_tasks = len(mission.tasks)
        if total_tasks == 0:
            return 0.0
        completed_tasks = [task for task in mission.tasks if task.status == TaskStatus.COMPLETE]
        failed_tasks = [task for task in mission.tasks if task.status == TaskStatus.FAILED]
        skipped_tasks = [task for task in mission.tasks if task.status == TaskStatus.SKIPPED]
        builder_results = [task.result for task in mission.tasks if task.assigned_ant in {"builder", "coder"} and task.result]
        verifier_results = [task.result for task in mission.tasks if task.assigned_ant == "verifier" and task.result]
        score = (len(completed_tasks) / total_tasks) - (len(failed_tasks) * 0.25) - (len(skipped_tasks) * 0.05)
        if len("\n".join(builder_results)) > self.QUALITY_BONUS_THRESHOLD:
            score += 0.10
        verdict = extract_verdict("\n".join(verifier_results))
        if verdict == "failed":
            score -= 0.25
        elif verdict == "needs_improvement":
            score -= 0.10
        elif verdict == "passed":
            score += 0.05
        return max(0.0, min(1.0, round(score, 2)))


class PatchProposalParser:
    def parse(self, raw_text: str, mission_id: str, task_id: str) -> PatchSet:
        parsed = extract_json_object(raw_text)
        summary = str(parsed.get("summary", "")).strip() or "Structured patch proposal generated by CoderAnt."
        raw_proposals = parsed.get("proposals", [])
        if not isinstance(raw_proposals, list):
            raise ValueError("'proposals' must be a list.")
        raw_proposals = raw_proposals[:MAX_PATCH_PROPOSALS_PER_SET]
        proposals: List[PatchProposal] = []
        for item in raw_proposals:
            if not isinstance(item, dict):
                continue
            file_path = validate_safe_patch_path(str(item.get("file_path", "")).strip())
            change_type = str(item.get("change_type", "modify")).strip().lower()
            reason = str(item.get("reason", "")).strip()
            risk = str(item.get("risk", "")).strip()
            old_content = item.get("old_content")
            new_content = item.get("new_content")
            if isinstance(old_content, str):
                old_content = truncate_text(old_content, MAX_PATCH_CONTENT_CHARS, suffix="...[old_content truncated]")
            if isinstance(new_content, str):
                new_content = truncate_text(new_content, MAX_PATCH_CONTENT_CHARS, suffix="...[new_content truncated]")
            if not reason:
                raise ValueError("Patch proposal missing reason.")
            if not risk:
                risk = "Unspecified risk. Human review required."
            proposals.append(PatchProposal(file_path=file_path, change_type=change_type, reason=reason, risk=risk, old_content=old_content, new_content=new_content, requires_approval=True, status=PatchStatus.PROPOSED))
        return PatchSet(mission_id=mission_id, task_id=task_id, summary=summary, proposals=proposals)


# ============================================================
#  ANTS
# ============================================================

class BaseAnt(ABC):
    def __init__(self, name: str):
        self.name = name

    @abstractmethod
    def run(self, task: Task, mission: Mission) -> str:
        pass


class ResearcherAnt(BaseAnt):
    def __init__(self, memory: SQLiteMemory, tools: ToolRegistry, model_router: Optional[ModelRouter] = None):
        super().__init__(name="researcher")
        self.memory = memory
        self.tools = tools
        self.model_router = model_router

    def run(self, task: Task, mission: Mission) -> str:
        recent_memory = self.memory.format_recent_memory(RECENT_MEMORY_LIMIT, MEMORY_RESULT_CHARS)
        relevant_memory = self.memory.format_relevant_memory(mission.goal, RELEVANT_MEMORY_LIMIT, MEMORY_RESULT_CHARS)
        pheromone_context = self.memory.format_pheromone_context(limit=8)
        tool_results = [self.tools.run_tool("system_info", mission_id=mission.id, task_id=task.id, ant_name=self.name)]
        if self._should_inspect_workspace(task, mission):
            tool_results.append(self.tools.run_tool("list_directory", mission_id=mission.id, task_id=task.id, ant_name=self.name, path="."))

        raw_context = (
            f"Mission: {mission.goal}\n\n"
            f"Task: {task.description}\n\n"
            f"Recent Memory:\n{recent_memory}\n\n"
            f"Relevant Memory:\n{relevant_memory}\n\n"
            f"Pheromone Trails:\n{pheromone_context}\n\n"
            f"Tool Context:\n{self._format_tool_report(tool_results)}"
        )
        raw_context = truncate_text(raw_context, MAX_CONTEXT_PACKET_CHARS, suffix="...[research context truncated]")

        if self.model_router is None or not USE_OLLAMA:
            return (
                "Researcher Ant summarized local context without LLM routing.\n\n"
                + raw_context
                + "\n\nResearch Finding:\nANTHILL v1.4 supports read-only external research when ENABLE_WEB_SEARCH=True."
            )

        prompt = f"""
ANTHILL v1.4 | role: researcher | timestamp: {now_utc().isoformat()} | mission: {truncate_text(mission.goal, 180)}
You are concise. Do not explain your reasoning unless asked.

Summarize only the context that is relevant to the mission below.
Do not repeat the mission goal back to the user.
Produce a tight context brief for downstream ants.
Aim for 150-300 words unless more is required.

Context:
{raw_context}

Return format:
- Relevant Memory:
- Useful Tool Context:
- Pheromone Guidance:
- Research Need:
"""
        response = self.model_router.generate("researcher", prompt, mission_id=mission.id, task_id=task.id, ant_name=self.name)
        if response.startswith("ERROR:"):
            return "Researcher routed model unavailable. Fallback context brief:\n\n" + raw_context
        return response

    def _should_inspect_workspace(self, task: Task, mission: Mission) -> bool:
        text = f"{mission.goal} {task.title} {task.description}".lower()
        keywords = ["file", "folder", "directory", "workspace", "project", "code", "script", "python", "repo", "repository", "debug", "error", "config", "read", "inspect", "list", "show", "look at", "open", "tree", "structure", "patch"]
        return any(keyword in text for keyword in keywords)

    def _format_tool_report(self, results: List[ToolResult]) -> str:
        blocks = []
        for result in results:
            if result.success:
                blocks.append(f"Tool: {result.tool_name}\nSuccess: {result.success}\nOutput:\n{result.output}")
            else:
                blocks.append(f"Tool: {result.tool_name}\nSuccess: {result.success}\nError:\n{result.error}")
        return "\n\n---\n\n".join(blocks)


class SourceQualityEngine:
    """Scores web sources so ANTHILL's external research stays useful at scale.

    This is intentionally heuristic in v1.3.1. Later versions can replace this with
    learned source-quality pheromones, embedding-based relevance, and verifier feedback.
    """

    RECENT_HINTS = {"2026", "2025", "latest", "current", "release", "updated", "today", "recent"}
    STALE_HINTS = {"2019", "2018", "2017", "2016", "2015", "archived", "deprecated"}

    def score(self, goal: str, title: str, url: str, snippet: str) -> Dict[str, Any]:
        decoded_url = decode_search_url(url)
        domain = extract_domain(decoded_url)
        text = f"{title} {decoded_url} {snippet}".lower()
        goal_keywords = extract_keywords(goal)
        source_keywords = extract_keywords(text)

        relevance = 0.25
        if goal_keywords:
            overlap = goal_keywords.intersection(source_keywords)
            relevance = min(1.0, 0.25 + (len(overlap) / max(4, len(goal_keywords))))

        authority = 0.35
        if domain in SOURCE_ALLOWLIST_DOMAINS:
            authority = 0.95
        elif any(domain.endswith(suffix) for suffix in HIGH_AUTHORITY_DOMAIN_SUFFIXES):
            authority = 0.85
        elif any(keyword in domain for keyword in HIGH_AUTHORITY_DOMAIN_KEYWORDS):
            authority = 0.75
        elif domain in SOURCE_BLOCKLIST_DOMAINS:
            authority = 0.05

        freshness = 0.5
        if any(hint in text for hint in self.RECENT_HINTS):
            freshness = 0.8
        if any(hint in text for hint in self.STALE_HINTS):
            freshness = 0.25

        confidence = round((relevance * 0.45) + (authority * 0.35) + (freshness * 0.20), 3)
        label = self._label(confidence)

        notes = []
        if domain in SOURCE_ALLOWLIST_DOMAINS:
            notes.append("allowlist authority boost")
        if domain in SOURCE_BLOCKLIST_DOMAINS:
            notes.append("blocklisted domain")
        if goal_keywords and not goal_keywords.intersection(source_keywords):
            notes.append("low keyword overlap")
        if any(hint in text for hint in self.STALE_HINTS):
            notes.append("possible stale source")
        if not notes:
            notes.append("heuristic score")

        return {
            "domain": domain,
            "url": decoded_url,
            "relevance_score": round(relevance, 3),
            "freshness_score": round(freshness, 3),
            "authority_score": round(authority, 3),
            "confidence_score": confidence,
            "confidence_label": label,
            "quality_notes": "; ".join(notes),
        }

    def should_skip(self, domain: str) -> bool:
        return domain in SOURCE_BLOCKLIST_DOMAINS

    def _label(self, score: float) -> str:
        if score >= 0.78:
            return "high"
        if score >= 0.55:
            return "medium"
        return "low"


class WebResearchAnt(BaseAnt):
    def __init__(self, memory: SQLiteMemory, tools: ToolRegistry, model_router: Optional[ModelRouter] = None):
        super().__init__(name="web")
        self.memory = memory
        self.tools = tools
        self.model_router = model_router
        self.quality_engine = SourceQualityEngine()

    def run(self, task: Task, mission: Mission) -> str:
        existing_source_count = self.memory.count_sources_for_mission(mission.id)
        if existing_source_count >= MAX_SOURCES_PER_MISSION:
            return (
                f"WebResearchAnt skipped search because the mission source budget is exhausted.\n"
                f"Mission Sources: {existing_source_count}/{MAX_SOURCES_PER_MISSION}"
            )

        existing_search_count = self.memory.count_web_search_attempts_for_mission(mission.id)
        if existing_search_count >= MAX_WEB_SEARCHES_PER_MISSION:
            self.memory.log_event(
                mission_id=mission.id,
                task_id=task.id,
                ant_name=self.name,
                event_type="web_search_budget_exhausted",
                message="WebResearchAnt skipped search because the mission web-search attempt budget is exhausted.",
                metadata={
                    "web_search_attempts": existing_search_count,
                    "max_web_searches_per_mission": MAX_WEB_SEARCHES_PER_MISSION,
                },
            )
            return (
                f"WebResearchAnt skipped search because the mission web-search attempt budget is exhausted.\n"
                f"Web Searches: {existing_search_count}/{MAX_WEB_SEARCHES_PER_MISSION}"
            )

        query = self._build_query(task, mission)
        self.memory.log_event(
            mission_id=mission.id,
            task_id=task.id,
            ant_name=self.name,
            event_type="web_search_attempted",
            message="WebResearchAnt requested read-only external research.",
            metadata={
                "query": query,
                "attempt_number": existing_search_count + 1,
                "max_web_searches_per_mission": MAX_WEB_SEARCHES_PER_MISSION,
                "max_sources_per_search": MAX_SOURCES_PER_SEARCH,
            },
        )
        result = self.tools.run_tool(
            "web_search",
            mission_id=mission.id,
            task_id=task.id,
            ant_name=self.name,
            query=query,
            max_results=min(MAX_WEB_RESULTS, MAX_SOURCES_PER_SEARCH),
        )
        if not result.success:
            return (
                f"WebResearchAnt could not perform external research.\n"
                f"Query: {query}\n"
                f"Error: {result.error}\n"
                f"Note: Set ENABLE_WEB_SEARCH=True to allow read-only web search."
            )

        try:
            payload = json.loads(result.output or "{}")
        except Exception:
            payload = {"query": query, "results": []}

        saved_sources: List[SourceRecord] = []
        seen_urls: set[str] = set()
        skipped_count = 0

        for item in payload.get("results", [])[:MAX_SOURCES_PER_SEARCH]:
            title = str(item.get("title") or "Untitled source")
            raw_url = str(item.get("url") or "")
            url = decode_search_url(raw_url)
            snippet = str(item.get("snippet") or "")
            if not url:
                continue

            dedupe_key = normalize_url_for_dedupe(url)
            if dedupe_key in seen_urls:
                skipped_count += 1
                continue
            seen_urls.add(dedupe_key)

            quality = self.quality_engine.score(mission.goal, title, url, snippet)
            domain = quality["domain"]
            if self.quality_engine.should_skip(domain):
                skipped_count += 1
                continue

            summary = self._summarize_source(mission.goal, title, url, snippet, quality)
            source = SourceRecord(
                id=source_id_from_url(url),
                mission_id=mission.id,
                task_id=task.id,
                ant_name=self.name,
                title=title,
                url=url,
                domain=domain,
                snippet=truncate_text(snippet, MAX_SOURCE_SUMMARY_CHARS),
                summary=truncate_text(summary, MAX_SOURCE_SUMMARY_CHARS),
                provider=WEB_SEARCH_PROVIDER,
                relevance_score=quality["relevance_score"],
                freshness_score=quality["freshness_score"],
                authority_score=quality["authority_score"],
                confidence_score=quality["confidence_score"],
                confidence_label=quality["confidence_label"],
                quality_notes=quality["quality_notes"],
            )
            self.memory.save_source_record(source)
            saved_sources.append(source)

            source_delta = 0.02 if source.confidence_score >= 0.55 else -0.005
            self.memory.update_pheromone_trail(
                trail_key=f"source_domain:{source.domain}",
                trail_type="source_domain",
                success=source.confidence_score >= 0.55,
                strength_delta=source_delta,
                metadata={
                    "mission_id": mission.id,
                    "task_id": task.id,
                    "source_id": source.id,
                    "confidence_score": source.confidence_score,
                    "confidence_label": source.confidence_label,
                },
            )

        self.memory.update_pheromone_trail(
            trail_key=f"tool:web_search:{WEB_SEARCH_PROVIDER}",
            trail_type="external_research_tool",
            success=bool(saved_sources),
            strength_delta=0.02 if saved_sources else -0.01,
            metadata={
                "mission_id": mission.id,
                "task_id": task.id,
                "query": query,
                "source_count": len(saved_sources),
                "skipped_count": skipped_count,
            },
        )

        if not saved_sources:
            preview = payload.get("preview") or "No parsed search results returned."
            return f"WebResearchAnt ran query but saved no source records.\nQuery: {query}\nSkipped: {skipped_count}\nPreview:\n{truncate_text(preview, 1000)}"

        lines = [
            f"WebResearchAnt saved {len(saved_sources)} source record(s).",
            f"Query: {query}",
            f"Skipped/Deduped/Filtered: {skipped_count}",
        ]
        for src in saved_sources:
            lines.append(
                f"Source ID: {src.id}\n"
                f"Title: {src.title}\n"
                f"Domain: {src.domain}\n"
                f"Confidence: {src.confidence_label} ({src.confidence_score})\n"
                f"URL: {src.url}\n"
                f"Summary: {src.summary}"
            )
        return "\n\n---\n\n".join(lines)

    def _build_query(self, task: Task, mission: Mission) -> str:
        text = f"{mission.goal} {task.description}".strip()
        return truncate_text(text, 300, suffix="")

    def _summarize_source(self, goal: str, title: str, url: str, snippet: str, quality: Optional[Dict[str, Any]] = None) -> str:
        quality = quality or {}
        base = (
            f"Title: {title}\n"
            f"URL: {url}\n"
            f"Snippet: {snippet}\n"
            f"Quality: confidence={quality.get('confidence_score', 'n/a')} label={quality.get('confidence_label', 'n/a')} notes={quality.get('quality_notes', 'n/a')}"
        )
        if self.model_router is None or not USE_OLLAMA:
            return truncate_text(snippet or title, MAX_SOURCE_SUMMARY_CHARS)
        prompt = f"""
ANTHILL v1.4 | role: web | timestamp: {now_utc().isoformat()} | mission: {truncate_text(goal, 180)}
You are concise. Do not explain your reasoning unless asked.

Summarize why this source may be relevant to the mission in 1-3 sentences.
Include any obvious freshness or authority caveat.
Do not invent details beyond the title/snippet/url/quality fields.

Source:
{base}
"""
        response = self.model_router.generate("web", prompt, ant_name="web")
        if response.startswith("ERROR:"):
            return truncate_text(snippet or title, MAX_SOURCE_SUMMARY_CHARS)
        return truncate_text(response, MAX_SOURCE_SUMMARY_CHARS)



class FileAnt(BaseAnt):
    def __init__(self, tools: ToolRegistry):
        super().__init__(name="file")
        self.tools = tools

    def run(self, task: Task, mission: Mission) -> str:
        directory_result = self.tools.run_tool("list_directory", mission_id=mission.id, task_id=task.id, ant_name=self.name, path=".")
        candidate_paths = self._extract_candidate_paths(task, mission) if self._should_attempt_file_reads(task, mission) else []
        file_reports = []
        for path in candidate_paths[:MAX_FILEANT_FILES_TO_READ]:
            read_result = self.tools.run_tool("read_text_file", mission_id=mission.id, task_id=task.id, ant_name=self.name, path=path)
            if read_result.success:
                file_reports.append(f"File: {path}\nRead Success: True\nContent:\n{read_result.output}")
            else:
                file_reports.append(f"File: {path}\nRead Success: False\nError:\n{read_result.error}")
        if not file_reports:
            file_reports.append("FileAnt did not identify specific readable file paths. It only listed workspace structure.")
        return (
            f"FileAnt performed safe read-only workspace inspection.\n\n"
            f"Mission:\n{mission.goal}\n\nAssigned Task:\n{task.description}\n\n"
            f"Workspace Listing:\nSuccess: {directory_result.success}\n{directory_result.output if directory_result.success else directory_result.error}\n\n"
            f"File Read Reports:\n" + "\n\n---\n\n".join(file_reports) +
            "\n\nFileAnt did not write, modify, delete, execute, or patch any files."
        )

    def _should_attempt_file_reads(self, task: Task, mission: Mission) -> bool:
        text = f"{mission.goal} {task.title} {task.description}".lower()
        keywords = ["read", "open", "inspect", "review", "check", "debug", "analyze", "look at", "show me", "this file", "my file", "script", "code", "repo", "repository", "project", "patch"]
        return any(keyword in text for keyword in keywords)

    def _extract_candidate_paths(self, task: Task, mission: Mission) -> List[str]:
        text = f"{mission.goal}\n{task.title}\n{task.description}"
        candidates = []
        candidates.extend(re.findall(r"['\"]([^'\"]+\.[A-Za-z0-9]+)['\"]", text))
        suffix_pattern = r"\b[\w\-/\\.]+\.(?:py|txt|md|json|yaml|yml|toml|ini|cfg|log|csv|html|css|js|ts|tsx|jsx|xml)\b"
        candidates.extend(re.findall(suffix_pattern, text))
        lowered = text.lower()
        if any(keyword in lowered for keyword in ["anthill", "this script", "main script", "python script"]):
            candidates.append("anthill.py")
        cleaned, seen = [], set()
        for candidate in candidates:
            candidate = candidate.strip().strip(".,;:()[]{}")
            if candidate and candidate not in seen:
                cleaned.append(candidate)
                seen.add(candidate)
        return cleaned


class CoderAnt(BaseAnt):
    def __init__(self, use_ollama: bool = USE_OLLAMA, model_router: Optional[ModelRouter] = None):
        super().__init__(name="coder")
        self.use_ollama = use_ollama
        self.model_router = model_router

    def run(self, task: Task, mission: Mission) -> str:
        code_context = self._build_code_context(mission)
        if not self.use_ollama or self.model_router is None:
            return self._fallback_patch_json("CoderAnt fallback mode produced no patch proposals because model routing/LLM generation is unavailable.")
        prompt = f"""
ANTHILL v1.4 | role: coder | timestamp: {now_utc().isoformat()} | mission: {truncate_text(mission.goal, 180)}
You are concise. Do not explain your reasoning unless asked.

You are Coder Ant inside ANTHILL v1.4.

Your role:
Create structured patch proposals as JSON only.

Limits:
- You do not write files.
- You do not run shell commands.
- You do not apply patches.
- You only propose patches.
- Patch application happens later through /apply after approval and config gates.

Mission goal:
{mission.goal}

Assigned task:
{task.title}
{task.description}

Prior context:
{code_context}

Return ONLY valid JSON.

Required format:
{{
  "summary": "Brief summary.",
  "proposals": [
    {{
      "file_path": "relative/path/to/file.py",
      "change_type": "modify",
      "reason": "Why this change is recommended.",
      "risk": "Risk level and what should be reviewed.",
      "old_content": "Exact old content for modify, or null for add.",
      "new_content": "Proposed new content.",
      "requires_approval": true
    }}
  ]
}}

Allowed change_type values:
add, modify, delete, rename

Example valid patch proposal:
{
  "summary": "Add a small helper function.",
  "proposals": [
    {
      "file_path": "anthill.py",
      "change_type": "modify",
      "reason": "This keeps repeated logic in one place.",
      "risk": "Low; verify exact old_content before applying.",
      "old_content": "def old_helper():\n    pass",
      "new_content": "def old_helper():\n    return True",
      "requires_approval": true
    }
  ]
}

Rules:
- Prefer modify or add.
- If you are unsure of the exact old_content, return an empty proposals list rather than guessing.
- Do not wrap JSON in markdown code fences.
- For modify, old_content must be exact and unambiguous.
- For add, old_content should be null.
- Do not propose database, .git, venv, cache, or absolute paths.
- Do not propose paths containing ..
- Every proposal requires approval.
- If context is incomplete, return an empty proposals list.
"""
        response = self.model_router.generate("coder", prompt, mission_id=mission.id, task_id=task.id, ant_name=self.name)
        if response.startswith("ERROR:"):
            return self._fallback_patch_json(f"CoderAnt could not reach the routed model, so no patch proposals were created. Model error: {response}")
        return response

    def _build_code_context(self, mission: Mission) -> str:
        return build_context_packet_text(
            mission=mission,
            consumer_role="coder",
            max_total_chars=min(MAX_CODER_CONTEXT_CHARS, MAX_CONTEXT_PACKET_CHARS),
        )

    def _fallback_patch_json(self, summary: str) -> str:
        return json.dumps({"summary": summary, "proposals": []}, indent=2)


class BuilderAnt(BaseAnt):
    def __init__(self, use_ollama: bool = USE_OLLAMA, model_router: Optional[ModelRouter] = None):
        super().__init__(name="builder")
        self.use_ollama = use_ollama
        self.model_router = model_router

    def run(self, task: Task, mission: Mission) -> str:
        previous_context = build_context_packet_text(
            mission=mission,
            consumer_role="builder",
            max_total_chars=min(MAX_PREVIOUS_CONTEXT_CHARS, MAX_CONTEXT_PACKET_CHARS),
        )
        if not self.use_ollama or self.model_router is None:
            return self._fallback_response(task, mission, previous_context)
        prompt = f"""
ANTHILL v1.4 | role: builder | timestamp: {now_utc().isoformat()} | mission: {truncate_text(mission.goal, 180)}
You are concise. Do not explain your reasoning unless asked.

You are Builder Ant inside ANTHILL v1.4.

Mission goal:
{mission.goal}

Assigned task:
{task.title}
{task.description}

Prior context:
{previous_context}

Create a practical final response.

Rules:
- Lead with the direct answer before any explanation.
- Do not repeat the mission goal back to the user.
- Aim for 200-400 words unless the task requires more.
- Be direct.
- Do not claim files were changed unless /apply actually ran.
- If patch proposals exist, say they can be inspected with /patches and /patch <patch_id>.
- Explain that approved patches can be applied with /apply <approval_id> only if config write gates are enabled.
- Mention that v1.3.1 supports dependency-aware parallel execution, FTS memory search, and role-based model routing.
"""
        response = self.model_router.generate("builder", prompt, mission_id=mission.id, task_id=task.id, ant_name=self.name)
        if response.startswith("ERROR:"):
            return f"{response}\n\nFallback Builder Response:\n{self._fallback_response(task, mission, previous_context)}"
        return response

    def _fallback_response(self, task: Task, mission: Mission, previous_context: str) -> str:
        return (
            f"Builder Ant created a basic non-LLM response.\n\n"
            f"Mission Goal:\n{mission.goal}\n\nAssigned Task:\n{task.title}\n{task.description}\n\n"
            f"Previous Context:\n{previous_context}\n\n"
            f"Proposed Output:\n"
            f"1. Review patch proposals using /patches and /patch <patch_id>.\n"
            f"2. Approve with /approve <approval_id>.\n"
            f"3. Apply with /apply <approval_id> only after enabling write gates.\n"
            f"4. v1.3 can run eligible independent tasks in parallel, uses FTS5 when available, and routes model calls by role."
        )


class VerifierAnt(BaseAnt):
    def __init__(self, use_ollama: bool = USE_OLLAMA, model_router: Optional[ModelRouter] = None):
        super().__init__(name="verifier")
        self.use_ollama = use_ollama
        self.model_router = model_router

    def run(self, task: Task, mission: Mission) -> str:
        prior_tasks = [t for t in mission.tasks if t.id != task.id]
        completed_tasks = [t for t in prior_tasks if t.status == TaskStatus.COMPLETE]
        failed_tasks = [t for t in prior_tasks if t.status == TaskStatus.FAILED]
        output_tasks = [t for t in prior_tasks if t.assigned_ant in {"builder", "coder"} and t.result]
        static_check = self._static_verify(completed_tasks, failed_tasks, output_tasks)
        if not self.use_ollama or self.model_router is None:
            return static_check
        context = self._build_verification_context(mission, prior_tasks)
        prompt = f"""
ANTHILL v1.4 | role: verifier | timestamp: {now_utc().isoformat()} | mission: {truncate_text(mission.goal, 180)}
You are concise. Do not explain your reasoning unless asked.

You are Verifier Ant inside ANTHILL v1.4.

Mission goal:
{mission.goal}

Static system check:
{static_check}

Task outputs:
{context}

Return:
- Verdict: Verification Passed / Needs Improvement / Verification Failed
- Reasoning:
- Missing Steps:
- Risk Notes:

Rules:
- Check whether the builder actually answered the specific question asked, not merely whether output exists.
- If the builder response contains only procedural ANTHILL commands like /apply, /patches, or /approval, mark Needs Improvement.
- If patch proposals exist, confirm they were only proposed.
- If /apply was not executed, do not claim files were modified.
- If v1.3 write gates are disabled, confirm patch application cannot run.
"""
        response = self.model_router.generate("verifier", prompt, mission_id=mission.id, task_id=task.id, ant_name=self.name)
        if response.startswith("ERROR:"):
            return f"{static_check}\n\nRouted verifier model unavailable:\n{response}"
        return response

    def _static_verify(self, completed_tasks: List[Task], failed_tasks: List[Task], output_tasks: List[Task]) -> str:
        if failed_tasks:
            return "Verification Failed\nReasoning: One or more tasks failed before verification.\nMissing Steps: Resolve failed task output before finalizing.\nRisk Notes: Mission may be incomplete or partially invalid."
        if not output_tasks:
            return "Verification Failed\nReasoning: No builder or coder output was found to verify.\nMissing Steps: Builder or coder output is required.\nRisk Notes: Mission result may be empty or incomplete."
        if len(completed_tasks) >= 2:
            return "Verification Passed\nReasoning: Mission has completed task output and at least one builder/coder result.\nMissing Steps: None identified by static verification.\nRisk Notes: Static verification does not evaluate factual content."
        return "Needs Improvement\nReasoning: Mission may not have enough completed task output.\nMissing Steps: More task output may be needed before finalizing.\nRisk Notes: Output may be incomplete."

    def _build_verification_context(self, mission: Mission, prior_tasks: List[Task]) -> str:
        return build_context_packet_text(
            mission=mission,
            consumer_role="verifier",
            max_total_chars=min(MAX_VERIFIER_CONTEXT_CHARS, MAX_CONTEXT_PACKET_CHARS),
        )


# ============================================================
#  QUEEN
# ============================================================

class Queen:
    # ANTHILL alignment:
    # The Queen is the central coordinator: plan, dispatch, verify, remember, and score.
    # It should stay thin enough to orchestrate, while ants/tools carry specialized behavior.
    def __init__(self):
        self.memory = SQLiteMemory()
        self.model_router = ModelRouter(memory=self.memory) if ENABLE_MODEL_ROUTING else None
        self.tools = self._build_tool_registry()
        self.planner = Planner(use_ollama=USE_OLLAMA, model_router=self.model_router)
        self.pheromones = PheromoneEngine()
        self.patch_parser = PatchProposalParser()
        self.execution_lock = Lock()
        self.ants = {
            "researcher": ResearcherAnt(memory=self.memory, tools=self.tools, model_router=self.model_router),
            "web": WebResearchAnt(memory=self.memory, tools=self.tools, model_router=self.model_router),
            "file": FileAnt(tools=self.tools),
            "coder": CoderAnt(use_ollama=USE_OLLAMA, model_router=self.model_router),
            "builder": BuilderAnt(use_ollama=USE_OLLAMA, model_router=self.model_router),
            "verifier": VerifierAnt(use_ollama=USE_OLLAMA, model_router=self.model_router),
        }

    def _build_tool_registry(self) -> ToolRegistry:
        registry = ToolRegistry(memory=self.memory)
        path_guard = WorkspacePathGuard(ALLOWED_WORKSPACE_ROOT)
        registry.register(SystemInfoTool())
        if ENABLE_FILE_TOOLS:
            registry.register(DirectoryListTool(path_guard=path_guard))
            registry.register(ReadTextFileTool(path_guard=path_guard))
        registry.register(WebSearchTool())
        registry.register(ShellCommandTool())
        registry.register(ApplyPatchTool(path_guard=path_guard))
        return registry

    def run_mission(self, goal: str) -> str:
        print(f"[bold cyan]Queen received mission:[/bold cyan] {goal}")
        mission_started_at = now_utc()
        mission = Mission(goal=goal, status=MissionStatus.RUNNING)
        self.memory.log_event(mission_id=mission.id, event_type="mission_created", message="Mission created.", metadata={"goal": goal})

        memory_context = (
            f"Recent Memory:\n{self.memory.format_recent_memory(RECENT_MEMORY_LIMIT, MEMORY_RESULT_CHARS)}\n\n"
            f"Relevant Memory:\n{self.memory.format_relevant_memory(goal, RELEVANT_MEMORY_LIMIT, MEMORY_RESULT_CHARS)}"
        )
        mission.tasks = self.planner.create_tasks(
            goal,
            memory_context=memory_context,
            tool_context=self.tools.describe_tools(),
            pheromone_context=self.memory.format_pheromone_context(limit=8),
        )
        for task in mission.tasks:
            if task.task_type == "general":
                task.task_type = infer_task_type(task.assigned_ant, task.title, task.description)
        if ENABLE_AUTO_DEPENDENCY_WIRING:
            self._auto_wire_dependencies(mission)

        for task in mission.tasks:
            self.memory.log_event(
                mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                event_type="task_created", message=f"Task created for {task.assigned_ant}: {task.title}",
                metadata={"task_type": task.task_type, "depends_on": task.depends_on, "parent_task_ids": task.parent_task_ids},
            )

        self.memory.log_event(
            mission_id=mission.id, event_type="mission_started", message="Mission execution started.",
            metadata={
                "task_count": len(mission.tasks),
                "planner_pattern": [task.assigned_ant for task in mission.tasks],
                "task_type_pattern": [task.task_type for task in mission.tasks],
                "parallel_execution": ENABLE_PARALLEL_EXECUTION,
                "max_parallel_workers": MAX_PARALLEL_WORKERS,
                "auto_dependency_wiring": ENABLE_AUTO_DEPENDENCY_WIRING,
            },
        )
        print(f"[dim]Mission ID: {mission.id}[/dim]")
        print(f"[dim]Created {len(mission.tasks)} tasks.[/dim]")
        print(f"[dim]Parallel execution: {'ON' if ENABLE_PARALLEL_EXECUTION else 'OFF'}[/dim]\n")

        if ENABLE_PARALLEL_EXECUTION:
            self._execute_tasks_parallel(mission, mission_started_at)
        else:
            self._execute_tasks_sequential(mission, mission_started_at)

        self._finalize_mission(mission)
        print(f"[bold blue]Pheromone score:[/bold blue] {mission.success_score}")
        self.memory.save_mission(mission)
        self.memory.log_event(mission_id=mission.id, event_type="mission_saved", message="Mission saved to ANTHILL memory.", metadata={"db_path": self.memory.db_path})
        print("[bold magenta]Mission saved to ANTHILL memory.[/bold magenta]")
        return self._compose_cli_result(mission)

    def _auto_wire_dependencies(self, mission: Mission):
        researcher_file_ids: List[str] = []
        pre_builder_ids: List[str] = []
        builder_ids: List[str] = []
        for task in mission.tasks:
            if task.depends_on:
                continue
            if task.assigned_ant in {"researcher", "web", "file"}:
                pass
            elif task.assigned_ant == "coder":
                task.depends_on = list(researcher_file_ids)
            elif task.assigned_ant == "builder":
                task.depends_on = list(pre_builder_ids)
            elif task.assigned_ant == "verifier":
                task.depends_on = list(pre_builder_ids + builder_ids)
            if task.assigned_ant in {"researcher", "web", "file"}:
                researcher_file_ids.append(task.id)
                pre_builder_ids.append(task.id)
            elif task.assigned_ant == "coder":
                pre_builder_ids.append(task.id)
            elif task.assigned_ant == "builder":
                builder_ids.append(task.id)

    def _execute_tasks_sequential(self, mission: Mission, mission_started_at: datetime):
        for index, task in enumerate(mission.tasks, start=1):
            if self._mission_timed_out(mission_started_at):
                self._skip_pending_for_timeout(mission)
                return
            unmet_dependencies = self._get_unmet_dependencies(task, mission)
            if unmet_dependencies:
                self._skip_task_for_dependencies(task, mission, unmet_dependencies)
                continue
            self._run_single_task(task, mission, index, len(mission.tasks))

    def _execute_tasks_parallel(self, mission: Mission, mission_started_at: datetime):
        running: Dict[Future, Task] = {}
        task_index = {task.id: index for index, task in enumerate(mission.tasks, start=1)}
        last_timeout_sweep = 0.0
        with ThreadPoolExecutor(max_workers=max(1, MAX_PARALLEL_WORKERS)) as executor:
            while True:
                if self._mission_timed_out(mission_started_at):
                    with self.execution_lock:
                        self._skip_pending_for_timeout(mission)
                    for future in running:
                        future.cancel()
                    return

                now_monotonic = time.monotonic()
                if now_monotonic - last_timeout_sweep >= TASK_TIMEOUT_SWEEP_SECONDS:
                    last_timeout_sweep = now_monotonic
                    with self.execution_lock:
                        for running_task in list(running.values()):
                            if running_task.status == TaskStatus.RUNNING and running_task.started_at:
                                elapsed = (now_utc() - running_task.started_at).total_seconds()
                                if elapsed > MAX_TASK_SECONDS:
                                    self._mark_task_timeout(running_task, mission)

                with self.execution_lock:
                    pending = [task for task in mission.tasks if task.status == TaskStatus.PENDING]
                    if not pending and not running:
                        return
                    eligible = []
                    for task in pending:
                        unmet = self._get_unmet_dependencies(task, mission)
                        if unmet:
                            # If dependencies are impossible because the dependency task failed/skipped/missing, skip.
                            if self._dependencies_are_terminally_unmet(unmet, mission):
                                self._skip_task_for_dependencies(task, mission, unmet)
                            continue
                        eligible.append(task)

                    open_slots = max(0, MAX_PARALLEL_WORKERS - len(running))
                    to_submit = list(eligible[:open_slots])

                for task in to_submit:
                    future = executor.submit(self._run_single_task, task, mission, task_index.get(task.id, 0), len(mission.tasks))
                    running[future] = task

                if not running:
                    time.sleep(0.05)
                    continue

                done = [future for future in running if future.done()]
                if not done:
                    time.sleep(0.05)
                    continue

                for future in done:
                    task = running.pop(future)
                    try:
                        future.result()
                    except Exception as error:
                        with self.execution_lock:
                            if task.status == TaskStatus.RUNNING:
                                task.status = TaskStatus.FAILED
                                task.result = f"Task failed with unhandled parallel error: {error}"
                                task.finished_at = now_utc()
                                if task.started_at:
                                    task.elapsed_seconds = round((task.finished_at - task.started_at).total_seconds(), 3)
                                self._finalize_task_result(mission, task)
                                self.memory.log_event(
                                    mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                                    event_type="task_failed", message=task.result,
                                    metadata={"task_type": task.task_type, "error": str(error), "elapsed_seconds": task.elapsed_seconds},
                                )

    def _mission_timed_out(self, mission_started_at: datetime) -> bool:
        return (now_utc() - mission_started_at).total_seconds() > MAX_MISSION_SECONDS

    def _skip_pending_for_timeout(self, mission: Mission):
        for task in mission.tasks:
            if task.status == TaskStatus.PENDING:
                task.status = TaskStatus.SKIPPED
                task.result = "Task skipped because mission timed out."
                task.finished_at = now_utc()
                task.elapsed_seconds = 0.0
                self._finalize_task_result(mission, task)
                self.memory.log_event(
                    mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                    event_type="task_skipped_timeout", message=task.result,
                    metadata={"task_type": task.task_type},
                )

    def _run_single_task(self, task: Task, mission: Mission, index: int, total: int):
        task_started_at = now_utc()
        with self.execution_lock:
            if task.status != TaskStatus.PENDING:
                return
            task.status = TaskStatus.RUNNING
            task.started_at = task_started_at
            task.finished_at = None
            task.elapsed_seconds = None
            print(f"[yellow]Task {index}/{total} → {task.assigned_ant} ant:[/yellow] {task.title}")
            self.memory.log_event(
                mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                event_type="task_started", message=f"Task started: {task.title}",
                metadata={
                    "task_type": task.task_type,
                    "index": index,
                    "parallel": ENABLE_PARALLEL_EXECUTION,
                    "max_task_seconds": MAX_TASK_SECONDS,
                    "snapshot_context": True,
                },
            )
            task_snapshot = pydantic_deep_copy(task)
            mission_snapshot = self._snapshot_mission_locked(mission)

        ant = self.ants.get(task.assigned_ant)
        if not ant:
            with self.execution_lock:
                task.status = TaskStatus.FAILED
                task.result = f"No ant found for role: {task.assigned_ant}"
                task.finished_at = now_utc()
                task.elapsed_seconds = round((task.finished_at - task_started_at).total_seconds(), 3)
                self._finalize_task_result(mission, task)
                self.memory.log_event(mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant, event_type="task_failed", message=task.result, metadata={"reason": "missing_ant", "elapsed_seconds": task.elapsed_seconds})
                print(f"[red]{task.result}[/red]")
            return

        try:
            # v1.3: ants receive a locked snapshot of the mission, not the live shared task list.
            result = ant.run(task_snapshot, mission_snapshot)
            finished_at = now_utc()
            elapsed = round((finished_at - task_started_at).total_seconds(), 3)
            with self.execution_lock:
                # If the parallel scheduler already marked this task failed due to timeout,
                # do not let a late worker overwrite the terminal state.
                if task.status != TaskStatus.RUNNING:
                    self.memory.log_event(
                        mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                        event_type="task_late_result_ignored",
                        message=f"Late result ignored for task already in terminal/non-running state: {task.status.value}",
                        metadata={"elapsed_seconds": elapsed, "result_preview": truncate_text(result or "", 500)},
                    )
                    return
                task.result = result
                task.finished_at = finished_at
                task.elapsed_seconds = elapsed
                if elapsed > MAX_TASK_SECONDS:
                    task.status = TaskStatus.FAILED
                    task.result = f"Task exceeded max runtime of {MAX_TASK_SECONDS} seconds. Elapsed: {elapsed} seconds."
                    self._finalize_task_result(mission, task)
                    self.memory.log_event(
                        mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                        event_type="task_failed_timeout",
                        message=task.result,
                        metadata={"task_type": task.task_type, "elapsed_seconds": elapsed, "max_task_seconds": MAX_TASK_SECONDS},
                    )
                    print(f"[red]{task.result}[/red]")
                    return
                task.status = TaskStatus.COMPLETE
                self._finalize_task_result(mission, task)
                self.memory.log_event(
                    mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                    event_type="task_completed", message=f"Task completed: {task.title}",
                    metadata={"task_type": task.task_type, "elapsed_seconds": elapsed, "result_preview": truncate_text(task.result or "", 500)},
                )
                if task.assigned_ant == "coder":
                    self._process_patch_proposals(mission, task)
                print(f"[green]Task complete:[/green] {task.title} ({elapsed}s)")
        except Exception as error:
            finished_at = now_utc()
            elapsed = round((finished_at - task_started_at).total_seconds(), 3)
            with self.execution_lock:
                if task.status != TaskStatus.RUNNING:
                    self.memory.log_event(
                        mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                        event_type="task_late_error_ignored",
                        message=f"Late error ignored for task already in terminal/non-running state: {task.status.value}",
                        metadata={"elapsed_seconds": elapsed, "error": str(error)},
                    )
                    return
                task.status = TaskStatus.FAILED
                task.result = f"Task failed with error: {error}"
                task.finished_at = finished_at
                task.elapsed_seconds = elapsed
                self._finalize_task_result(mission, task)
                self.memory.log_event(
                    mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                    event_type="task_failed", message=task.result,
                    metadata={"task_type": task.task_type, "error": str(error), "elapsed_seconds": elapsed},
                )
                print(f"[red]{task.result}[/red]")

    def _snapshot_mission_locked(self, mission: Mission) -> Mission:
        # Caller must hold execution_lock. This gives ants a stable, read-only-ish view
        # of task state so parallel workers do not iterate over a mutating live list.
        return pydantic_deep_copy(mission)

    def _mark_task_timeout(self, task: Task, mission: Mission):
        now = now_utc()
        task.status = TaskStatus.FAILED
        task.finished_at = now
        if task.started_at:
            task.elapsed_seconds = round((now - task.started_at).total_seconds(), 3)
        task.result = f"Task exceeded max runtime of {MAX_TASK_SECONDS} seconds."
        self._finalize_task_result(mission, task)
        self.memory.log_event(
            mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
            event_type="task_failed_timeout", message=task.result,
            metadata={"task_type": task.task_type, "elapsed_seconds": task.elapsed_seconds, "max_task_seconds": MAX_TASK_SECONDS},
        )
        print(f"[red]{task.result}[/red]")

    def _finalize_task_result(self, mission: Mission, task: Task):
        task.result_chars = len(task.result or "")
        task.estimated_tokens = estimate_token_count(task.result)
        task.result_summary = create_result_summary(task.result, MAX_RESULT_SUMMARY_CHARS)
        self.memory.save_task_result_summary(mission.id, task)
        self.memory.log_message_metric(
            mission_id=mission.id,
            task_id=task.id,
            ant_name=task.assigned_ant,
            metric_type="task_result",
            input_chars=len(task.description or ""),
            output_chars=task.result_chars,
            metadata={
                "task_type": task.task_type,
                "status": task.status.value,
                "summary_chars": len(task.result_summary or ""),
                "context_packets_enabled": ENABLE_CONTEXT_PACKETS,
            },
        )
        self.memory.log_event(
            mission_id=mission.id,
            task_id=task.id,
            ant_name=task.assigned_ant,
            event_type="task_result_summarized",
            message=f"Task result summarized for compact downstream context: {task.title}",
            metadata={
                "result_chars": task.result_chars,
                "summary_chars": len(task.result_summary or ""),
                "estimated_tokens": task.estimated_tokens,
            },
        )

    def _dependencies_are_terminally_unmet(self, unmet_dependencies: List[str], mission: Mission) -> bool:
        task_by_id = {task.id: task for task in mission.tasks}
        for dep_id in unmet_dependencies:
            dep_task = task_by_id.get(dep_id)
            if dep_task is None:
                return True
            if dep_task.status in {TaskStatus.FAILED, TaskStatus.SKIPPED}:
                return True
            if dep_task.status in {TaskStatus.PENDING, TaskStatus.RUNNING}:
                return False
        return False

    def _skip_task_for_dependencies(self, task: Task, mission: Mission, unmet_dependencies: List[str]):
        task.status = TaskStatus.SKIPPED
        task.result = f"Task skipped because dependencies were not complete: {unmet_dependencies}"
        task.finished_at = now_utc()
        task.elapsed_seconds = 0.0
        self._finalize_task_result(mission, task)
        self.memory.log_event(
            mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
            event_type="task_skipped_dependency", message=task.result,
            metadata={"task_type": task.task_type, "unmet_dependencies": unmet_dependencies},
        )
        print(f"[yellow]{task.result}[/yellow]")

    def _process_patch_proposals(self, mission: Mission, task: Task):
        if not task.result:
            return
        try:
            patch_set = self.patch_parser.parse(task.result, mission.id, task.id)
            self.memory.save_patch_set(patch_set)
            self.memory.log_event(
                mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                event_type="patch_set_created", message=f"Patch set created with {len(patch_set.proposals)} proposal(s).",
                metadata={"patch_set_id": patch_set.id, "proposal_count": len(patch_set.proposals), "summary": patch_set.summary, "saved": True},
            )
            if not patch_set.proposals:
                self.memory.log_event(
                    mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                    event_type="patch_set_empty", message="CoderAnt returned a valid patch set with no proposals.",
                    metadata={"patch_set_id": patch_set.id, "summary": patch_set.summary},
                )
                self.memory.update_pheromone_trail(
                    trail_key="capability:structured_patch_proposals", trail_type="capability", success=True,
                    strength_delta=0.005, metadata={"mission_id": mission.id, "task_id": task.id, "proposal_count": 0, "reason": "valid_empty_patch_set"},
                )
                return
            for proposal in patch_set.proposals:
                self.memory.log_event(
                    mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                    event_type="patch_proposal_created", message=f"Patch proposal created for {proposal.file_path}",
                    metadata={"patch_set_id": patch_set.id, "patch_proposal_id": proposal.id, "file_path": proposal.file_path, "change_type": proposal.change_type.value, "requires_approval": proposal.requires_approval, "status": proposal.status.value},
                )
                approval = self._create_patch_approval_request(mission, task, patch_set, proposal)
                self.memory.save_approval_request(approval)
                self.memory.log_event(
                    mission_id=mission.id, task_id=task.id, ant_name="queen",
                    event_type="approval_request_created", message=f"Approval request created for patch proposal: {proposal.file_path}",
                    metadata={"approval_request_id": approval.id, "target_id": approval.target_id, "action_type": approval.action_type.value, "approval_status": approval.status.value},
                )
            self.memory.update_pheromone_trail(
                trail_key="capability:structured_patch_proposals", trail_type="capability", success=True,
                strength_delta=0.03, metadata={"mission_id": mission.id, "task_id": task.id, "proposal_count": len(patch_set.proposals), "approval_requests_created": len(patch_set.proposals)},
            )
            self.memory.update_pheromone_trail(
                trail_key="capability:approval_gate", trail_type="capability", success=True,
                strength_delta=0.02, metadata={"mission_id": mission.id, "task_id": task.id, "approval_requests_created": len(patch_set.proposals)},
            )
        except (ValueError, ValidationError, json.JSONDecodeError) as error:
            self.memory.log_event(
                mission_id=mission.id, task_id=task.id, ant_name=task.assigned_ant,
                event_type="patch_proposal_parse_failed", message=f"Patch proposal parsing failed: {error}",
                metadata={"error": str(error), "raw_preview": truncate_text(task.result, 1000)},
            )
            self.memory.update_pheromone_trail(
                trail_key="capability:structured_patch_proposals", trail_type="capability", success=False,
                strength_delta=-0.03, metadata={"mission_id": mission.id, "task_id": task.id, "error": str(error)},
            )

    def _create_patch_approval_request(self, mission: Mission, task: Task, patch_set: PatchSet, proposal: PatchProposal) -> ApprovalRequest:
        return ApprovalRequest(
            mission_id=mission.id, task_id=task.id, action_type=ApprovalActionType.PATCH_PROPOSAL,
            target_id=proposal.id, title=f"Approve patch proposal for {proposal.file_path}",
            description=(
                f"Patch proposal requires approval before application.\n"
                f"File: {proposal.file_path}\nChange Type: {proposal.change_type.value}\n"
                f"Reason: {proposal.reason}\nRisk: {proposal.risk}\n\n"
                f"v1.3 note: approval alone does not apply the patch. Use /apply <approval_id> after approval and after enabling write gates."
            ),
            metadata={"patch_set_id": patch_set.id, "patch_proposal_id": proposal.id, "file_path": proposal.file_path, "change_type": proposal.change_type.value, "requires_approval": proposal.requires_approval, "patch_application_enabled": ENABLE_PATCH_APPLICATION, "file_writing_enabled": ENABLE_FILE_WRITING},
        )

    def _finalize_mission(self, mission: Mission):
        has_failed_tasks = any(task.status == TaskStatus.FAILED for task in mission.tasks)
        has_skipped_tasks = any(task.status == TaskStatus.SKIPPED for task in mission.tasks)
        if has_failed_tasks:
            mission.status = MissionStatus.FAILED
        elif has_skipped_tasks:
            mission.status = MissionStatus.PARTIAL
        else:
            mission.status = MissionStatus.COMPLETE
        mission.success_score = self.pheromones.score_mission(mission)
        self.memory.log_event(mission_id=mission.id, event_type="pheromone_scored", message=f"Mission pheromone score calculated: {mission.success_score}", metadata={"success_score": mission.success_score, "mission_status": mission.status.value})
        self.memory.update_mission_pheromones(mission)
        mission.best_output_task_id = self._select_best_output_task_id(mission)
        mission.user_result = self._compose_user_result(mission)
        mission.debug_result = self._compose_debug_result(mission)
        mission.final_result = mission.user_result
        self.memory.log_event(mission_id=mission.id, event_type="best_output_selected", message=f"Best output task selected: {mission.best_output_task_id}", metadata={"best_output_task_id": mission.best_output_task_id})
        event_type = "mission_completed" if mission.status == MissionStatus.COMPLETE else "mission_partial" if mission.status == MissionStatus.PARTIAL else "mission_failed"
        self.memory.log_event(
            mission_id=mission.id, event_type=event_type, message=f"Mission finished with status: {mission.status.value}",
            metadata={"success_score": mission.success_score, "task_count": len(mission.tasks), "failed_tasks": [task.id for task in mission.tasks if task.status == TaskStatus.FAILED], "skipped_tasks": [task.id for task in mission.tasks if task.status == TaskStatus.SKIPPED], "best_output_task_id": mission.best_output_task_id},
        )

    def approve_request(self, approval_id: str) -> str:
        try:
            approval_id = validate_approval_id(approval_id)
        except ValueError as error:
            return f"Invalid approval id: {error}"
        approval = self.memory.get_approval_request(approval_id)
        if not approval:
            return f"No approval request found with id: {approval_id}"
        if approval["status"] != ApprovalStatus.PENDING.value:
            return f"Approval request is not pending.\nID: {approval_id}\nCurrent Status: {approval['status']}"
        updated = self.memory.update_approval_status(approval_id, ApprovalStatus.APPROVED, "Approved by user. Patch can only be applied through /apply if write gates are enabled.")
        if updated:
            self.memory.log_event(mission_id=updated["mission_id"], task_id=updated.get("task_id"), ant_name="queen", event_type="approval_request_approved", message=f"Approval request approved: {approval_id}", metadata={"approval_request_id": approval_id, "action_type": updated["action_type"], "target_id": updated["target_id"], "patch_application_enabled": ENABLE_PATCH_APPLICATION, "file_writing_enabled": ENABLE_FILE_WRITING})
        return f"Approval recorded.\nID: {approval_id}\nStatus: approved\n\nNext step: inspect the patch with /patch {updated['target_id']}.\nTo apply later: /apply {approval_id}\n\nPatch application requires ENABLE_PATCH_APPLICATION=True and ENABLE_FILE_WRITING=True."

    def reject_request(self, approval_id: str, reason: Optional[str] = None) -> str:
        try:
            approval_id = validate_approval_id(approval_id)
        except ValueError as error:
            return f"Invalid approval id: {error}"
        approval = self.memory.get_approval_request(approval_id)
        if not approval:
            return f"No approval request found with id: {approval_id}"
        if approval["status"] != ApprovalStatus.PENDING.value:
            return f"Approval request is not pending.\nID: {approval_id}\nCurrent Status: {approval['status']}"
        note = reason or "Rejected by user."
        updated = self.memory.update_approval_status(approval_id, ApprovalStatus.REJECTED, note)
        if updated:
            if updated["action_type"] == ApprovalActionType.PATCH_PROPOSAL.value:
                self.memory.update_patch_status(patch_id=updated["target_id"], status=PatchStatus.REJECTED, last_error=note)
            self.memory.log_event(mission_id=updated["mission_id"], task_id=updated.get("task_id"), ant_name="queen", event_type="approval_request_rejected", message=f"Approval request rejected: {approval_id}", metadata={"approval_request_id": approval_id, "action_type": updated["action_type"], "target_id": updated["target_id"], "reason": note})
        return f"Approval request rejected.\nID: {approval_id}\nStatus: rejected\nReason: {note}"

    def apply_approved_patch(self, approval_id: str) -> str:
        try:
            approval_id = validate_approval_id(approval_id)
        except ValueError as error:
            return f"Invalid approval id: {error}"
        approval = self.memory.get_approval_request(approval_id)
        if not approval:
            return f"No approval request found with id: {approval_id}"
        if approval["status"] != ApprovalStatus.APPROVED.value:
            return f"Cannot apply patch. Approval request is not approved.\nID: {approval_id}\nCurrent Status: {approval['status']}"
        if approval["action_type"] != ApprovalActionType.PATCH_PROPOSAL.value:
            return f"Cannot apply approval type: {approval['action_type']}\nv1.3 only applies patch_proposal approvals."
        patch_id = approval["target_id"]
        patch = self.memory.get_patch_proposal(patch_id)
        if not patch:
            return f"No patch proposal found for approval target id: {patch_id}"
        if patch["status"] == PatchStatus.APPLIED.value:
            return f"Patch is already applied.\nPatch ID: {patch_id}"
        if patch["status"] in {PatchStatus.REJECTED.value, PatchStatus.FAILED.value}:
            return f"Patch cannot be applied because status is {patch['status']}.\nPatch ID: {patch_id}"
        result = self.tools.run_tool("apply_patch", mission_id=approval["mission_id"], task_id=approval.get("task_id"), ant_name="queen", patch=patch)
        if not result.success:
            self.memory.update_patch_status(patch_id=patch_id, status=PatchStatus.FAILED, last_error=result.error)
            self.memory.log_event(mission_id=approval["mission_id"], task_id=approval.get("task_id"), ant_name="queen", event_type="patch_apply_failed", message=f"Patch application failed: {patch_id}", metadata={"approval_request_id": approval_id, "patch_id": patch_id, "error": result.error})
            return f"Patch application failed.\nApproval ID: {approval_id}\nPatch ID: {patch_id}\nError: {result.error}"
        try:
            output_data = json.loads(result.output or "{}")
        except Exception:
            output_data = {}
        backup_path = output_data.get("backup_path")
        self.memory.update_patch_status(patch_id=patch_id, status=PatchStatus.APPLIED, applied_at=now_utc().isoformat(), backup_path=backup_path, last_error=None)
        self.memory.update_approval_status(approval_id=approval_id, new_status=ApprovalStatus.CONSUMED, decision_note="Approval consumed by successful patch application.")
        self.memory.log_event(mission_id=approval["mission_id"], task_id=approval.get("task_id"), ant_name="queen", event_type="patch_applied", message=f"Patch applied successfully: {patch_id}", metadata={"approval_request_id": approval_id, "patch_id": patch_id, "file_path": patch["file_path"], "change_type": patch["change_type"], "backup_path": backup_path})
        self.memory.update_pheromone_trail(trail_key="capability:controlled_file_writing", trail_type="capability", success=True, strength_delta=0.03, metadata={"approval_request_id": approval_id, "patch_id": patch_id, "file_path": patch["file_path"]})
        return f"Patch applied successfully.\nApproval ID: {approval_id}\nPatch ID: {patch_id}\nFile: {patch['file_path']}\nBackup: {backup_path or 'n/a'}\nApproval Status: consumed\nPatch Status: applied"

    def format_pending_approvals(self, limit: int = 20) -> str:
        approvals = self.memory.list_approval_requests(status=ApprovalStatus.PENDING, limit=limit)
        if not approvals:
            return "No pending approval requests."
        blocks = []
        for approval in approvals:
            metadata = self._parse_metadata(approval)
            blocks.append(f"Approval ID: {approval['id']}\nStatus: {approval['status']}\nAction Type: {approval['action_type']}\nTarget ID: {approval['target_id']}\nTitle: {approval['title']}\nFile: {metadata.get('file_path', 'n/a')}\nCreated At: {approval['created_at']}\nInspect: /approval {approval['id']}")
        return "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_approval_detail(self, approval_id: str) -> str:
        try:
            approval_id = validate_approval_id(approval_id)
        except ValueError as error:
            return f"Invalid approval id: {error}"
        approval = self.memory.get_approval_request(approval_id)
        if not approval:
            return f"No approval request found with id: {approval_id}"
        metadata = self._parse_metadata(approval)
        target_id = approval.get("target_id")
        related_patch_line = ""
        apply_line = ""
        if approval.get("action_type") == ApprovalActionType.PATCH_PROPOSAL.value:
            related_patch_line = f"\nInspect Related Patch: /patch {target_id}"
            apply_line = f"\nApply If Approved: /apply {approval_id}"
        return (
            f"Approval ID: {approval.get('id')}\nStatus: {approval.get('status')}\nAction Type: {approval.get('action_type')}\nTarget ID: {target_id}\nMission ID: {approval.get('mission_id')}\nTask ID: {approval.get('task_id')}\nRequested By: {approval.get('requested_by')}\nTitle: {approval.get('title')}\n\n"
            f"Description:\n{approval.get('description')}\n\nDecision Note:\n{approval.get('decision_note') or 'n/a'}\n\nCreated At: {approval.get('created_at')}\nDecided At: {approval.get('decided_at') or 'n/a'}\n\nMetadata:\n{json.dumps(metadata, indent=2)}{related_patch_line}{apply_line}\n\nv1.3 Safety Note: /apply only works when approval is approved and both write gates are enabled."
        )

    def format_patch_list(self, limit: int = PATCH_LIST_LIMIT_DEFAULT) -> str:
        patches = self.memory.list_patch_proposals(limit=limit)
        if not patches:
            return "No patch proposals found."
        blocks = []
        for patch in patches:
            reason_preview = truncate_text(patch.get("reason") or "", 160, suffix="...[reason truncated]")
            approval = self.memory.get_approval_for_target(patch["id"], ApprovalActionType.PATCH_PROPOSAL)
            approval_line = f"Approval: {approval['status']} | {approval['id']}" if approval else "Approval: none"
            blocks.append(f"Patch ID: {patch.get('id')}\nStatus: {patch.get('status')}\nFile: {patch.get('file_path')}\nChange Type: {patch.get('change_type')}\nMission ID: {patch.get('mission_id')}\nTask ID: {patch.get('task_id')}\n{approval_line}\nApplied At: {patch.get('applied_at') or 'n/a'}\nBackup: {patch.get('backup_path') or 'n/a'}\nCreated At: {patch.get('created_at')}\nReason Preview:\n{reason_preview}\nInspect: /patch {patch.get('id')}")
        return "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_patch_detail(self, patch_id: str) -> str:
        try:
            patch_id = validate_patch_id(patch_id)
        except ValueError as error:
            return f"Invalid patch id: {error}"
        patch = self.memory.get_patch_proposal(patch_id)
        if not patch:
            return f"No patch proposal found with id: {patch_id}"
        approval = self.memory.get_approval_for_target(patch_id, ApprovalActionType.PATCH_PROPOSAL)
        if approval:
            approval_block = f"Related Approval ID: {approval.get('id')}\nRelated Approval Status: {approval.get('status')}\nInspect Approval: /approval {approval.get('id')}\nApply If Approved: /apply {approval.get('id')}"
        else:
            approval_block = "Related Approval: none"
        old_content = truncate_text(patch.get("old_content") or "", MAX_PATCH_DISPLAY_CHARS, suffix="...[old_content display truncated]")
        new_content = truncate_text(patch.get("new_content") or "", MAX_PATCH_DISPLAY_CHARS, suffix="...[new_content display truncated]")
        mission_goal = truncate_text(patch.get("mission_goal") or "", 500, suffix="...[mission goal truncated]")
        return (
            f"Patch ID: {patch.get('id')}\nPatch Set ID: {patch.get('patch_set_id')}\nMission ID: {patch.get('mission_id')}\nTask ID: {patch.get('task_id')}\nFile Path: {patch.get('file_path')}\nChange Type: {patch.get('change_type')}\nStatus: {patch.get('status')}\nRequires Approval: {bool(patch.get('requires_approval'))}\nCreated At: {patch.get('created_at')}\nApplied At: {patch.get('applied_at') or 'n/a'}\nBackup Path: {patch.get('backup_path') or 'n/a'}\nLast Error: {patch.get('last_error') or 'n/a'}\n\n"
            f"{approval_block}\n\nMission Goal:\n{mission_goal}\n\nPatch Set Summary:\n{patch.get('patch_set_summary') or 'n/a'}\n\nReason:\n{patch.get('reason')}\n\nRisk:\n{patch.get('risk')}\n\nOld Content:\n{old_content if old_content else '[empty or not provided]'}\n\nNew Content:\n{new_content if new_content else '[empty or not provided]'}\n\nv1.3 Safety Note: Use /apply <approval_id> to apply an approved patch when write gates are enabled."
        )

    def format_mission_history(self, limit: int = HISTORY_LIMIT_DEFAULT) -> str:
        missions = self.memory.get_recent_missions(limit=limit)
        if not missions:
            return "No mission history found."
        blocks = []
        for mission in missions:
            goal = truncate_text(mission.get("goal") or "", 160, suffix="...[goal truncated]")
            result = truncate_text(mission.get("user_result") or mission.get("final_result") or "", 200)
            blocks.append(f"Mission ID: {mission.get('id')}\nStatus: {mission.get('status')}\nPheromone Score: {mission.get('success_score')}\nBest Output Task ID: {mission.get('best_output_task_id') or 'n/a'}\nSaved At: {mission.get('saved_at')}\nGoal: {goal}\nResult Preview:\n{result}")
        return "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_memory_view(self, limit: int = MEMORY_LIST_LIMIT_DEFAULT) -> str:
        missions = self.memory.get_recent_missions(limit=limit)
        if not missions:
            return "No mission memory found."
        blocks = []
        for mission in missions:
            goal = truncate_text(mission.get("goal") or "", 220, suffix="...[goal truncated]")
            result = truncate_text(mission.get("user_result") or mission.get("final_result") or "", 300, suffix="...[result truncated]")
            blocks.append(
                f"Mission ID: {mission.get('id')}\n"
                f"Status: {mission.get('status')}\n"
                f"Pheromone Score: {mission.get('success_score')}\n"
                f"Best Output Task ID: {mission.get('best_output_task_id') or 'n/a'}\n"
                f"Saved At: {mission.get('saved_at')}\n"
                f"Goal:\n{goal}\n"
                f"Result Preview:\n{result}"
            )
        return "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_pheromone_view(self, limit: int = PHEROMONE_LIST_LIMIT_DEFAULT) -> str:
        trails = self.memory.get_top_pheromone_trails(limit=limit)
        if not trails:
            return "No pheromone trails found."
        blocks = []
        for trail in trails:
            blocks.append(
                f"Trail Key: {trail.get('trail_key')}\n"
                f"Trail Type: {trail.get('trail_type')}\n"
                f"Strength: {trail.get('strength')}\n"
                f"Success Count: {trail.get('success_count')}\n"
                f"Failure Count: {trail.get('failure_count')}\n"
                f"Last Updated: {trail.get('last_updated')}"
            )
        return "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_system_status(self) -> str:
        return (
            f"ANTHILL v1.4 Status\n"
            f"Original Goal Alignment: Queen + specialized ants + observable memory + pheromone learning\n"
            f"Model Routing: {'ON' if ENABLE_MODEL_ROUTING else 'OFF'}\n"
            f"Context Packets: {'ON' if ENABLE_CONTEXT_PACKETS else 'OFF'}\n"
            f"Result Summaries: {'ON' if ENABLE_RESULT_SUMMARIES else 'OFF'}\n"
            f"Message Metrics: {'ON' if ENABLE_MESSAGE_METRICS else 'OFF'}\n"
            f"Max Context Packet Chars: {MAX_CONTEXT_PACKET_CHARS}\n"
            f"Default Model Provider: {DEFAULT_MODEL_PROVIDER}\n"
            f"Parallel Execution: {'ON' if ENABLE_PARALLEL_EXECUTION else 'OFF'}\n"
            f"Max Parallel Workers: {MAX_PARALLEL_WORKERS}\n"
            f"Auto Dependency Wiring: {'ON' if ENABLE_AUTO_DEPENDENCY_WIRING else 'OFF'}\n"
            f"FTS Memory Requested: {'ON' if ENABLE_FTS_MEMORY else 'OFF'}\n"
            f"FTS Memory Available: {'ON' if self.memory.fts_available else 'OFF'}\n"
            f"Patch Application: {'ON' if ENABLE_PATCH_APPLICATION else 'OFF'}\n"
            f"File Writing: {'ON' if ENABLE_FILE_WRITING else 'OFF'}\n"
            f"Shell Tool: {'ON' if ENABLE_SHELL_TOOL else 'OFF'}\n"
            f"Workspace Root: {resolve_workspace_root()}\n"
            f"Visible Memory Commands: /memory, /pheromones\n"
            f"Event Visibility Commands: /events, /diagnostics\n"
            f"Write Gates Default Safe: patch application and file writing remain OFF unless manually enabled."
        )

    def format_message_metrics(self, limit: int = 20) -> str:
        summary = self.memory.summarize_message_metrics()
        rows = self.memory.get_recent_message_metrics(limit=limit)
        header = (
            f"ANTHILL v1.4 Message Efficiency Metrics\n"
            f"Metric Count: {summary.get('metric_count', 0)}\n"
            f"Total Input Chars: {summary.get('input_chars', 0)}\n"
            f"Total Output Chars: {summary.get('output_chars', 0)}\n"
            f"Estimated Input Tokens: {summary.get('input_tokens_est', 0)}\n"
            f"Estimated Output Tokens: {summary.get('output_tokens_est', 0)}\n"
            f"Context Packets: {'ON' if ENABLE_CONTEXT_PACKETS else 'OFF'}\n"
        )
        if not rows:
            return header + "\nNo message metrics recorded yet."
        blocks = []
        for row in rows:
            blocks.append(
                f"Metric ID: {row.get('id')}\n"
                f"Type: {row.get('metric_type')}\n"
                f"Mission ID: {row.get('mission_id')}\n"
                f"Task ID: {row.get('task_id') or 'n/a'}\n"
                f"Ant: {row.get('ant_name') or 'n/a'}\n"
                f"Input Chars: {row.get('input_chars')} | Output Chars: {row.get('output_chars')}\n"
                f"Input Tokens Est: {row.get('input_tokens_est')} | Output Tokens Est: {row.get('output_tokens_est')}\n"
                f"Created At: {row.get('created_at')}"
            )
        return header + "\n" + "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_task_metrics(self, limit: int = 20) -> str:
        summary = self.memory.summarize_task_metrics()
        rows = self.memory.get_recent_tasks(limit=limit)
        header = (
            f"ANTHILL v1.4 Task Runtime Metrics\n"
            f"Task Count: {summary.get('task_count', 0)}\n"
            f"Average Elapsed Seconds: {round(float(summary.get('avg_elapsed_seconds') or 0), 3)}\n"
            f"Max Elapsed Seconds: {round(float(summary.get('max_elapsed_seconds') or 0), 3)}\n"
            f"Failed Count: {summary.get('failed_count', 0)}\n"
            f"Skipped Count: {summary.get('skipped_count', 0)}\n"
            f"Max Task Seconds: {MAX_TASK_SECONDS}\n"
            f"Parallel Safety: locked mission snapshots ON\n"
        )
        if not rows:
            return header + "\nNo task metrics recorded yet."
        blocks = []
        for row in rows:
            goal_preview = truncate_text(row.get('mission_goal') or '', 120, suffix='...[goal truncated]')
            summary_preview = truncate_text(row.get('result_summary') or '', 220, suffix='...[summary truncated]')
            elapsed = row.get('elapsed_seconds')
            blocks.append(
                f"Task ID: {row.get('id')}\n"
                f"Mission ID: {row.get('mission_id')}\n"
                f"Ant: {row.get('assigned_ant')} | Type: {row.get('task_type')} | Status: {row.get('status')}\n"
                f"Title: {row.get('title')}\n"
                f"Elapsed Seconds: {elapsed if elapsed is not None else 'n/a'}\n"
                f"Result Chars: {row.get('result_chars')} | Estimated Tokens: {row.get('estimated_tokens')}\n"
                f"Started At: {row.get('started_at') or 'n/a'}\n"
                f"Finished At: {row.get('finished_at') or 'n/a'}\n"
                f"Mission Goal: {goal_preview}\n"
                f"Result Summary:\n{summary_preview}"
            )
        return header + "\n" + "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_sources(self, limit: int = SOURCE_LIST_LIMIT_DEFAULT) -> str:
        rows = self.memory.get_recent_sources(limit=limit)
        header = (
            f"ANTHILL v1.4 Source Records\n"
            f"Web Search Enabled: {'ON' if ENABLE_WEB_SEARCH else 'OFF'}\n"
            f"Provider: {WEB_SEARCH_PROVIDER}\n"
            f"Source Budget Per Mission: {MAX_SOURCES_PER_MISSION}\n"
            f"Web Search Attempt Budget Per Mission: {MAX_WEB_SEARCHES_PER_MISSION}\n"
            f"Limit: {limit}\n"
        )
        if not rows:
            return header + "\nNo source records found."
        blocks = []
        for row in rows:
            blocks.append(
                f"Source ID: {row.get('id')}\n"
                f"Created At: {row.get('created_at')}\n"
                f"Mission ID: {row.get('mission_id')}\n"
                f"Task ID: {row.get('task_id') or 'n/a'}\n"
                f"Ant: {row.get('ant_name') or 'n/a'}\n"
                f"Title: {row.get('title')}\n"
                f"Domain: {row.get('domain')}\n"
                f"Confidence: {row.get('confidence_label', 'unknown')} ({row.get('confidence_score', 0)})\n"
                f"Authority/Freshness/Relevance: {row.get('authority_score', 0)} / {row.get('freshness_score', 0)} / {row.get('relevance_score', 0)}\n"
                f"URL: {row.get('url')}\n"
                f"Summary:\n{truncate_text(row.get('summary') or row.get('snippet') or '', 500, suffix='...[source truncated]')}\n"
                f"Inspect: /source {row.get('id')}"
            )
        return header + "\n" + "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_source_detail(self, source_id: str) -> str:
        try:
            source_id = validate_source_id(source_id)
        except ValueError as error:
            return f"Invalid source id: {error}"
        row = self.memory.get_source_record(source_id)
        if not row:
            return f"No source record found with id: {source_id}"
        return (
            f"Source ID: {row.get('id')}\n"
            f"Created At: {row.get('created_at')}\n"
            f"Mission ID: {row.get('mission_id')}\n"
            f"Task ID: {row.get('task_id') or 'n/a'}\n"
            f"Ant: {row.get('ant_name') or 'n/a'}\n"
            f"Provider: {row.get('provider')}\n"
            f"Title: {row.get('title')}\n"
            f"Domain: {row.get('domain')}\n"
            f"URL: {row.get('url')}\n\n"
            f"Source Quality:\n"
            f"- Confidence: {row.get('confidence_label', 'unknown')} ({row.get('confidence_score', 0)})\n"
            f"- Relevance: {row.get('relevance_score', 0)}\n"
            f"- Freshness: {row.get('freshness_score', 0)}\n"
            f"- Authority: {row.get('authority_score', 0)}\n"
            f"- Notes: {row.get('quality_notes') or 'n/a'}\n\n"
            f"Snippet:\n{row.get('snippet') or 'n/a'}\n\n"
            f"Summary:\n{row.get('summary') or 'n/a'}\n"
        )

    def format_source_quality(self, limit: int = SOURCE_QUALITY_LIST_LIMIT_DEFAULT) -> str:
        rows = self.memory.get_source_quality_summary(limit=limit)
        header = (
            f"ANTHILL v1.4 Source Quality Summary\n"
            f"Purpose: track which domains provide useful external research context.\n"
            f"Limit: {limit}\n"
        )
        if not rows:
            return header + "\nNo source quality data found."
        blocks = []
        for row in rows:
            blocks.append(
                f"Domain: {row.get('domain')}\n"
                f"Sources: {row.get('source_count')}\n"
                f"Average Confidence: {row.get('avg_confidence')}\n"
                f"Average Authority: {row.get('avg_authority')}\n"
                f"Average Freshness: {row.get('avg_freshness')}\n"
                f"Average Relevance: {row.get('avg_relevance')}\n"
                f"Last Seen: {row.get('last_seen')}"
            )
        return header + "\n" + "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_event_log(
        self,
        limit: int = EVENT_LIST_LIMIT_DEFAULT,
        event_type: Optional[str] = None,
        mission_id: Optional[str] = None,
    ) -> str:
        rows = self.memory.get_recent_events(limit=limit, event_type=event_type, mission_id=mission_id)
        filter_bits = []
        if event_type:
            filter_bits.append(f"event_type={event_type}")
        if mission_id:
            filter_bits.append(f"mission_id={mission_id}")
        filter_line = " | ".join(filter_bits) if filter_bits else "none"
        header = (
            f"ANTHILL v1.4 Event Log\n"
            f"Limit: {limit}\n"
            f"Filters: {filter_line}\n"
            f"Purpose: CLI-level swarm observability before the future UI layer.\n"
        )
        if not rows:
            return header + "\nNo events found."
        blocks = []
        for row in rows:
            metadata = self._parse_metadata(row)
            metadata_preview = truncate_text(json.dumps(metadata, indent=2), 700, suffix="...[metadata truncated]")
            blocks.append(
                f"Event ID: {row.get('id')}\n"
                f"Created At: {row.get('created_at')}\n"
                f"Type: {row.get('event_type')}\n"
                f"Mission ID: {row.get('mission_id')}\n"
                f"Task ID: {row.get('task_id') or 'n/a'}\n"
                f"Ant: {row.get('ant_name') or 'n/a'}\n"
                f"Message: {row.get('message')}\n"
                f"Metadata:\n{metadata_preview}"
            )
        return header + "\n" + "\n\n" + ("\n" + "-" * 50 + "\n").join(blocks)

    def format_runtime_diagnostics(self) -> str:
        event_summary = self.memory.summarize_events()
        task_summary = self.memory.summarize_task_metrics()
        message_summary = self.memory.summarize_message_metrics()
        failure_events = self.memory.get_recent_failure_events(limit=DIAGNOSTIC_EVENT_LIMIT)
        top_events = event_summary.get("top_event_types") or []
        top_event_lines = [f"- {item.get('event_type')}: {item.get('count')}" for item in top_events]
        if not top_event_lines:
            top_event_lines = ["- none"]
        failure_blocks = []
        for row in failure_events:
            failure_blocks.append(
                f"{row.get('created_at')} | {row.get('event_type')} | "
                f"mission={row.get('mission_id')} | task={row.get('task_id') or 'n/a'} | "
                f"ant={row.get('ant_name') or 'n/a'} | {row.get('message')}"
            )
        if not failure_blocks:
            failure_blocks = ["No recent failure events recorded."]
        return (
            f"ANTHILL v1.4 Runtime Diagnostics\n"
            f"Goal Alignment: building toward ANTHILL OS with observable swarm execution, memory, and pheromone learning.\n\n"
            f"Execution Core:\n"
            f"- Parallel Execution: {'ON' if ENABLE_PARALLEL_EXECUTION else 'OFF'}\n"
            f"- Max Workers: {MAX_PARALLEL_WORKERS}\n"
            f"- Auto Dependency Wiring: {'ON' if ENABLE_AUTO_DEPENDENCY_WIRING else 'OFF'}\n"
            f"- Locked Snapshots: ON\n"
            f"- Max Mission Seconds: {MAX_MISSION_SECONDS}\n"
            f"- Max Task Seconds: {MAX_TASK_SECONDS}\n\n"
            f"Task Metrics:\n"
            f"- Task Count: {task_summary.get('task_count', 0)}\n"
            f"- Failed Tasks: {task_summary.get('failed_count', 0)}\n"
            f"- Skipped Tasks: {task_summary.get('skipped_count', 0)}\n"
            f"- Average Elapsed Seconds: {round(float(task_summary.get('avg_elapsed_seconds') or 0), 3)}\n"
            f"- Max Elapsed Seconds: {round(float(task_summary.get('max_elapsed_seconds') or 0), 3)}\n\n"
            f"Message Efficiency:\n"
            f"- Metric Count: {message_summary.get('metric_count', 0)}\n"
            f"- Total Input Chars: {message_summary.get('input_chars', 0)}\n"
            f"- Total Output Chars: {message_summary.get('output_chars', 0)}\n"
            f"- Estimated Input Tokens: {message_summary.get('input_tokens_est', 0)}\n"
            f"- Estimated Output Tokens: {message_summary.get('output_tokens_est', 0)}\n\n"
            f"Event Ledger:\n"
            f"- Event Count: {event_summary.get('event_count', 0)}\n"
            f"- Failure Event Count: {event_summary.get('failure_event_count', 0)}\n"
            f"- Task Completed Events: {event_summary.get('task_completed_count', 0)}\n"
            f"- Model Call Events: {event_summary.get('model_call_count', 0)}\n"
            f"- Last Event At: {event_summary.get('last_event_at') or 'n/a'}\n\n"
            f"Top Event Types:\n" + "\n".join(top_event_lines) + "\n\n"
            f"Recent Failure Events:\n" + "\n".join(failure_blocks) + "\n\n"
            f"Safety Gates:\n"
            f"- Patch Application: {'ON' if ENABLE_PATCH_APPLICATION else 'OFF'}\n"
            f"- File Writing: {'ON' if ENABLE_FILE_WRITING else 'OFF'}\n"
            f"- Shell Tool: {'ON' if ENABLE_SHELL_TOOL else 'OFF'}\n"
            f"- Web Search: {'ON' if ENABLE_WEB_SEARCH else 'OFF'}\n"
            f"- Max Web Searches Per Mission: {MAX_WEB_SEARCHES_PER_MISSION}\n"
            f"- Max Sources Per Mission: {MAX_SOURCES_PER_MISSION}\n"
        )

    def format_model_routes(self) -> str:
        if not self.model_router:
            return "Model routing is disabled."
        return self.model_router.format_routes()

    def format_model_status(self) -> str:
        if not self.model_router:
            return "Model routing is disabled."
        return self.model_router.format_models()

    def _parse_metadata(self, row: Dict[str, Any]) -> Dict[str, Any]:
        try:
            return json.loads(row.get("metadata_json") or "{}")
        except Exception:
            return {}

    def _get_unmet_dependencies(self, task: Task, mission: Mission) -> List[str]:
        if not task.depends_on:
            return []
        task_status_by_id = {existing_task.id: existing_task.status for existing_task in mission.tasks}
        return [dep_id for dep_id in task.depends_on if task_status_by_id.get(dep_id) != TaskStatus.COMPLETE]

    def _select_best_output_task_id(self, mission: Mission) -> Optional[str]:
        builder_tasks = [task for task in mission.tasks if task.assigned_ant == "builder" and task.status == TaskStatus.COMPLETE and task.result]
        if builder_tasks:
            return builder_tasks[-1].id
        coder_tasks = [task for task in mission.tasks if task.assigned_ant == "coder" and task.status == TaskStatus.COMPLETE and task.result]
        if coder_tasks:
            return coder_tasks[-1].id
        completed_tasks = [task for task in mission.tasks if task.status == TaskStatus.COMPLETE and task.result]
        if completed_tasks:
            return completed_tasks[-1].id
        return None

    def _compose_user_result(self, mission: Mission) -> str:
        if mission.best_output_task_id:
            for task in mission.tasks:
                if task.id == mission.best_output_task_id and task.result:
                    return task.result
        fallback_task_id = self._select_best_output_task_id(mission)
        if fallback_task_id:
            for task in mission.tasks:
                if task.id == fallback_task_id and task.result:
                    return task.result
        return "Mission produced no completed user-facing output."

    def _compose_debug_result(self, mission: Mission) -> str:
        parts = []
        for task in mission.tasks:
            parts.append(f"Task: {task.title}\nTask ID: {task.id}\nAnt: {task.assigned_ant}\nTask Type: {task.task_type}\nDepends On: {task.depends_on}\nParent Task IDs: {task.parent_task_ids}\nStatus: {task.status.value}\nResult Chars: {task.result_chars}\nEstimated Tokens: {task.estimated_tokens}\nResult Summary:\n{task.result_summary}\n\nFull Result:\n{task.result}\n")
        return "\n".join(parts)

    def _compose_cli_result(self, mission: Mission) -> str:
        mission_header = "Mission Complete" if mission.status == MissionStatus.COMPLETE else "Mission Partial" if mission.status == MissionStatus.PARTIAL else "Mission Failed"
        score_display = mission.success_score if mission.success_score is not None else "Not scored yet"
        debug_trace = mission.debug_result or ""
        if not SHOW_FULL_DEBUG_TRACE_IN_CLI:
            debug_trace = truncate_text(debug_trace, MAX_CLI_DEBUG_TRACE_CHARS, suffix="...[debug trace truncated for CLI; full trace saved in debug_result]")
        pending_approval_count = self.memory.count_pending_approvals()
        approval_note = f"\n\nPending Approval Requests: {pending_approval_count}\nUse /approvals to list them." if pending_approval_count else "\n\nPending Approval Requests: 0"
        return (
            f"{mission_header}\n\nGoal:\n{mission.goal}\n\nMission Status:\n{mission.status.value}\n\nPheromone Score:\n{score_display}\n\nBest Output Task ID:\n{mission.best_output_task_id or 'n/a'}\n\nUser Result:\n{mission.user_result}"
            f"{approval_note}\n\nDebug Trace:\n\n{debug_trace}"
        )




# ============================================================
#  V1.4 LOCAL REST API BACKEND
# ============================================================

class MissionRequest(BaseModel):
    goal: str


class RejectRequestBody(BaseModel):
    reason: Optional[str] = None


def clamp_limit(limit: int, default: int = API_DEFAULT_LIMIT) -> int:
    try:
        value = int(limit)
    except Exception:
        value = default
    return max(1, min(value, API_MAX_LIMIT))


def json_ok(data: Any = None, message: str = "ok") -> Dict[str, Any]:
    return {"ok": True, "data": data if data is not None else {}, "message": message, "error": None}


def json_error(message: str, error: Optional[str] = None, data: Any = None) -> Dict[str, Any]:
    return {"ok": False, "data": data, "message": message, "error": error or message}


def extract_api_token(authorization: Optional[str], x_anthill_token: Optional[str]) -> Optional[str]:
    if x_anthill_token:
        return x_anthill_token.strip()
    if authorization:
        value = authorization.strip()
        if value.lower().startswith("bearer "):
            return value[7:].strip()
        return value
    return None


def build_auth_dependency(queen: "Queen"):
    def require_api_auth(
        authorization: Optional[str] = Header(default=None),
        x_anthill_token: Optional[str] = Header(default=None),
    ) -> bool:
        if not ENABLE_API_AUTH:
            return True
        token = extract_api_token(authorization, x_anthill_token)
        if token == API_AUTH_TOKEN and API_AUTH_TOKEN != "":
            return True
        queen.memory.log_event(
            mission_id="api",
            event_type="api_auth_failed",
            message="API authentication failed.",
            ant_name="API",
            metadata={"has_authorization": bool(authorization), "has_x_anthill_token": bool(x_anthill_token)},
        )
        raise HTTPException(status_code=401, detail="API authentication required.")
    return require_api_auth


def api_permission_allowed(permission: str) -> bool:
    return bool(API_PERMISSIONS.get(permission, False))


def require_api_permission(queen: "Queen", permission: str, action: str) -> None:
    if api_permission_allowed(permission):
        return
    queen.memory.log_event(
        mission_id="api",
        event_type="api_permission_denied",
        message=f"API permission denied for action: {action}",
        ant_name="API",
        metadata={"permission": permission, "action": action},
    )
    raise HTTPException(status_code=403, detail=f"API permission denied: {permission}")


def log_api_action(queen: "Queen", event_type: str, message: str, metadata: Optional[Dict[str, Any]] = None) -> None:
    queen.memory.log_event(
        mission_id="api",
        event_type=event_type,
        message=message,
        ant_name="API",
        metadata=metadata or {},
    )


def create_api_app() -> "FastAPI":
    if FastAPI is None:
        raise RuntimeError(
            "FastAPI/uvicorn are not installed. Install with: pip install fastapi uvicorn"
        )

    # When this single-file module is imported dynamically, Pydantic forward refs can
    # need an explicit rebuild before API-triggered event logging.
    for model in (Event, Mission, Task, SourceRecord, PatchProposal, ApprovalRequest):
        try:
            model.model_rebuild(_types_namespace=globals())
        except Exception:
            pass

    app = FastAPI(
        title="ANTHILL Core API",
        version="1.4.1",
        description=(
            "Local-only API for ANTHILL Core. Exposes missions, memory, events, "
            "sources, pheromones, patches, approvals, diagnostics, and model routing."
        ),
    )

    if ENABLE_CORS:
        app.add_middleware(
            CORSMiddleware,
            allow_origins=API_ALLOW_ORIGINS,
            allow_credentials=True,
            allow_methods=["GET", "POST"],
            allow_headers=["*"],
        )

    queen = Queen()
    app.state.queen = queen
    require_api_auth = build_auth_dependency(queen)
    auth_dependency = Depends(require_api_auth)

    @app.get("/health")
    def api_health():
        return json_ok({
            "ok": True,
            "version": "1.4.1",
            "api_auth": ENABLE_API_AUTH,
            "api_host": API_HOST,
            "api_port": API_PORT,
            "cors_enabled": ENABLE_CORS,
            "web_search": ENABLE_WEB_SEARCH,
            "file_writing": ENABLE_FILE_WRITING,
            "patch_application": ENABLE_PATCH_APPLICATION,
            "shell_tool": ENABLE_SHELL_TOOL,
        }, message="ANTHILL API is online.")

    @app.get("/", dependencies=[auth_dependency])
    def api_root():
        log_api_action(queen, "api_request_received", "API root requested.", {"path": "/"})
        return json_ok({
            "name": "ANTHILL Core",
            "version": "1.4.1",
            "mode": "api",
            "local_only": True,
            "auth_required": ENABLE_API_AUTH,
            "docs": "/docs",
            "health": "/health",
            "status": "/status",
        })

    @app.get("/status", dependencies=[auth_dependency])
    def api_status():
        require_api_permission(queen, "read_status", "read status")
        log_api_action(queen, "api_request_received", "API status requested.", {"path": "/status"})
        return json_ok({
            "text": queen.format_system_status(),
            "config": {
                "api_host": API_HOST,
                "api_port": API_PORT,
                "web_search_enabled": ENABLE_WEB_SEARCH,
                "patch_application_enabled": ENABLE_PATCH_APPLICATION,
                "file_writing_enabled": ENABLE_FILE_WRITING,
                "shell_tool_enabled": ENABLE_SHELL_TOOL,
                "parallel_execution_enabled": ENABLE_PARALLEL_EXECUTION,
                "max_parallel_workers": MAX_PARALLEL_WORKERS,
                "model_routing_enabled": ENABLE_MODEL_ROUTING,
            },
        })

    @app.get("/diagnostics", dependencies=[auth_dependency])
    def api_diagnostics():
        require_api_permission(queen, "read_diagnostics", "read diagnostics")
        log_api_action(queen, "api_request_received", "API diagnostics requested.", {"path": "/diagnostics"})
        return json_ok({"text": queen.format_runtime_diagnostics()})

    @app.post("/missions", dependencies=[auth_dependency])
    def api_run_mission(request: MissionRequest):
        require_api_permission(queen, "run_mission", "run mission")
        goal = request.goal.strip()
        if not goal:
            raise HTTPException(status_code=400, detail="Mission goal is required.")
        if len(goal) > MAX_GOAL_LENGTH:
            raise HTTPException(status_code=400, detail=f"Mission goal exceeds {MAX_GOAL_LENGTH} characters.")
        log_api_action(queen, "api_mission_requested", "API mission requested.", {"goal_preview": goal[:200], "goal_chars": len(goal)})
        result = queen.run_mission(goal)
        return json_ok({"result": result}, message="Mission completed.")

    @app.get("/missions", dependencies=[auth_dependency])
    def api_missions(limit: int = API_DEFAULT_LIMIT):
        require_api_permission(queen, "read_memory", "read missions")
        return json_ok(queen.memory.get_recent_missions(limit=clamp_limit(limit)))

    @app.get("/memory", dependencies=[auth_dependency])
    def api_memory(limit: int = MEMORY_LIST_LIMIT_DEFAULT):
        require_api_permission(queen, "read_memory", "read memory")
        return json_ok({
            "rows": queen.memory.get_recent_missions(limit=clamp_limit(limit, MEMORY_LIST_LIMIT_DEFAULT)),
            "text": queen.format_memory_view(limit=clamp_limit(limit, MEMORY_LIST_LIMIT_DEFAULT)),
        })

    @app.get("/events", dependencies=[auth_dependency])
    def api_events(
        limit: int = EVENT_LIST_LIMIT_DEFAULT,
        event_type: Optional[str] = None,
        mission_id: Optional[str] = None,
    ):
        require_api_permission(queen, "read_events", "read events")
        rows = queen.memory.get_recent_events(
            limit=clamp_limit(limit, EVENT_LIST_LIMIT_DEFAULT),
            event_type=event_type,
            mission_id=mission_id,
        )
        return json_ok({
            "rows": rows,
            "text": queen.format_event_log(
                limit=clamp_limit(limit, EVENT_LIST_LIMIT_DEFAULT),
                event_type=event_type,
                mission_id=mission_id,
            ),
        })

    @app.get("/tasks", dependencies=[auth_dependency])
    def api_tasks(limit: int = 20):
        require_api_permission(queen, "read_tasks", "read tasks")
        return json_ok({
            "rows": queen.memory.get_recent_tasks(limit=clamp_limit(limit, 20)),
            "text": queen.format_task_metrics(limit=clamp_limit(limit, 20)),
        })

    @app.get("/messages", dependencies=[auth_dependency])
    def api_messages(limit: int = 20):
        require_api_permission(queen, "read_messages", "read messages")
        return json_ok({
            "rows": queen.memory.get_recent_message_metrics(limit=clamp_limit(limit, 20)),
            "text": queen.format_message_metrics(limit=clamp_limit(limit, 20)),
        })

    @app.get("/pheromones", dependencies=[auth_dependency])
    def api_pheromones(limit: int = PHEROMONE_LIST_LIMIT_DEFAULT):
        require_api_permission(queen, "read_pheromones", "read pheromones")
        return json_ok({
            "rows": queen.memory.get_top_pheromone_trails(limit=clamp_limit(limit, PHEROMONE_LIST_LIMIT_DEFAULT)),
            "text": queen.format_pheromone_view(limit=clamp_limit(limit, PHEROMONE_LIST_LIMIT_DEFAULT)),
        })

    @app.get("/models", dependencies=[auth_dependency])
    def api_models():
        require_api_permission(queen, "read_models", "read models")
        return json_ok({"text": queen.format_model_status(), "routes": MODEL_ROUTING})

    @app.get("/routes", dependencies=[auth_dependency])
    def api_routes():
        require_api_permission(queen, "read_models", "read model routes")
        return json_ok({"text": queen.format_model_routes(), "routes": MODEL_ROUTING})

    @app.get("/sources", dependencies=[auth_dependency])
    def api_sources(limit: int = SOURCE_LIST_LIMIT_DEFAULT):
        require_api_permission(queen, "read_sources", "read sources")
        return json_ok({
            "rows": queen.memory.get_recent_sources(limit=clamp_limit(limit, SOURCE_LIST_LIMIT_DEFAULT)),
            "text": queen.format_sources(limit=clamp_limit(limit, SOURCE_LIST_LIMIT_DEFAULT)),
        })

    @app.get("/sources/{source_id}", dependencies=[auth_dependency])
    def api_source_detail(source_id: str):
        require_api_permission(queen, "read_sources", "read source detail")
        try:
            clean_id = validate_source_id(source_id)
        except ValueError as error:
            raise HTTPException(status_code=400, detail=str(error))
        row = queen.memory.get_source_record(clean_id)
        if not row:
            raise HTTPException(status_code=404, detail="Source not found.")
        return json_ok({"row": row, "text": queen.format_source_detail(clean_id)})

    @app.get("/source-quality", dependencies=[auth_dependency])
    def api_source_quality(limit: int = SOURCE_QUALITY_LIST_LIMIT_DEFAULT):
        require_api_permission(queen, "read_sources", "read source quality")
        return json_ok({
            "rows": queen.memory.get_source_quality_summary(limit=clamp_limit(limit, SOURCE_QUALITY_LIST_LIMIT_DEFAULT)),
            "text": queen.format_source_quality(limit=clamp_limit(limit, SOURCE_QUALITY_LIST_LIMIT_DEFAULT)),
        })

    @app.get("/patches", dependencies=[auth_dependency])
    def api_patches(limit: int = PATCH_LIST_LIMIT_DEFAULT):
        require_api_permission(queen, "read_patches", "read patches")
        return json_ok({
            "rows": queen.memory.list_patch_proposals(limit=clamp_limit(limit, PATCH_LIST_LIMIT_DEFAULT)),
            "text": queen.format_patch_list(limit=clamp_limit(limit, PATCH_LIST_LIMIT_DEFAULT)),
        })

    @app.get("/patches/{patch_id}", dependencies=[auth_dependency])
    def api_patch_detail(patch_id: str):
        require_api_permission(queen, "read_patches", "read patch detail")
        try:
            clean_id = validate_patch_id(patch_id)
        except ValueError as error:
            raise HTTPException(status_code=400, detail=str(error))
        row = queen.memory.get_patch_proposal(clean_id)
        if not row:
            raise HTTPException(status_code=404, detail="Patch not found.")
        return json_ok({"row": row, "text": queen.format_patch_detail(clean_id)})

    @app.get("/approvals", dependencies=[auth_dependency])
    def api_approvals(limit: int = 20, status: str = "pending"):
        require_api_permission(queen, "read_approvals", "read approvals")
        status_map = {item.value: item for item in ApprovalStatus}
        status_enum = status_map.get(status.lower()) if status else ApprovalStatus.PENDING
        if status.lower() == "all":
            status_enum = None
        rows = queen.memory.list_approval_requests(status=status_enum, limit=clamp_limit(limit, 20))
        return json_ok({"rows": rows, "text": queen.format_pending_approvals(limit=clamp_limit(limit, 20)) if status_enum == ApprovalStatus.PENDING else rows})

    @app.get("/approvals/{approval_id}", dependencies=[auth_dependency])
    def api_approval_detail(approval_id: str):
        require_api_permission(queen, "read_approvals", "read approval detail")
        try:
            clean_id = validate_approval_id(approval_id)
        except ValueError as error:
            raise HTTPException(status_code=400, detail=str(error))
        row = queen.memory.get_approval_request(clean_id)
        if not row:
            raise HTTPException(status_code=404, detail="Approval not found.")
        return json_ok({"row": row, "text": queen.format_approval_detail(clean_id)})

    @app.post("/approvals/{approval_id}/approve", dependencies=[auth_dependency])
    def api_approve(approval_id: str):
        require_api_permission(queen, "approve", "approve request")
        log_api_action(queen, "api_approval_requested", "API approval requested.", {"approval_id": approval_id, "decision": "approve"})
        return json_ok({"text": queen.approve_request(approval_id)}, message="Approval request processed.")

    @app.post("/approvals/{approval_id}/reject", dependencies=[auth_dependency])
    def api_reject(approval_id: str, body: Optional[RejectRequestBody] = None):
        require_api_permission(queen, "reject", "reject request")
        reason = body.reason if body else None
        log_api_action(queen, "api_approval_requested", "API rejection requested.", {"approval_id": approval_id, "decision": "reject", "reason": reason})
        return json_ok({"text": queen.reject_request(approval_id, reason=reason)}, message="Rejection request processed.")

    @app.post("/approvals/{approval_id}/apply", dependencies=[auth_dependency])
    def api_apply(approval_id: str):
        require_api_permission(queen, "apply_patch", "apply patch")
        log_api_action(queen, "api_patch_apply_requested", "API patch apply requested.", {"approval_id": approval_id})
        return json_ok({"text": queen.apply_approved_patch(approval_id)}, message="Patch apply request processed.")

    return app


def run_api_server():
    if not ENABLE_API_SERVER:
        print("[red]API server is disabled by config.[/red]")
        return
    if uvicorn is None:
        print("[red]FastAPI/uvicorn are not installed.[/red]")
        print("Install with: pip install fastapi uvicorn")
        return
    app = create_api_app()
    print(f"[bold yellow]ANTHILL Core v1.4.1 API online at http://{API_HOST}:{API_PORT}[/bold yellow]")
    print("[dim]Local-only API mode. Use /docs for interactive endpoint docs.[/dim]")
    uvicorn.run(app, host=API_HOST, port=API_PORT)


# ============================================================
#  MAIN ENTRY POINT
# ============================================================

def print_help():
    print("""
[bold cyan]ANTHILL Commands[/bold cyan]

Mission:
  Type any normal mission and press Enter.

Approval Gate:
  /approvals
      Show pending approval requests.

  /approval <approval_id>
      Show one approval request in detail.

  /approve <approval_id>
      Mark an approval request as approved.

  /reject <approval_id> [reason]
      Reject an approval request with an optional reason.

Patch Inspection:
  /patches
      Show recent patch proposals.

  /patches <number>
      Show recent patch proposals with a custom limit.

  /patch <patch_id>
      Show one patch proposal in detail.

Patch Application:
  /apply <approval_id>
      Apply an approved patch proposal.
      Requires ENABLE_PATCH_APPLICATION=True and ENABLE_FILE_WRITING=True.
      v1.3.1 supports ADD and MODIFY only.
      MODIFY requires exact old_content and creates a backup.

History:
  /history
      Show recent mission history.

  /history <number>
      Show recent mission history with a custom limit.

System:
  /status
      Show runtime feature flags and memory capability status.

  /messages
      Show message-size and estimated-token metrics for ant communication.

  /messages <number>
      Show recent message metrics with a custom limit.

  /tasks
      Show recent task runtime metrics and summaries.

  /tasks <number>
      Show recent task metrics with a custom limit.

Event Diagnostics:
  /events
      Show recent event ledger entries.

  /events <number>
      Show recent event entries with a custom limit.

  /events type=<event_type>
      Filter event entries by type, such as type=task_failed.

  /diagnostics
      Show compact runtime diagnostics, recent failure events, and event summary.

Model Routing:
  /models
      Show model router status and active model targets.

  /routes
      Show role-to-model routing table.

External Research:
  /sources
      Show recent source records.

  /sources <number>
      Show recent source records with a custom limit.

  /source <source_id>
      Show one source record in detail.

  /source-quality
      Show domain-level source quality summary.

  /source-quality <number>
      Show source quality summary with a custom limit.

Memory Visibility:
  /memory
      Show recent saved mission memory.

  /memory <number>
      Show recent mission memory with a custom limit.

  /pheromones
      Show strongest pheromone trails.

  /pheromones <number>
      Show strongest pheromone trails with a custom limit.

  /help
      Show this help menu.

  exit
      Shut down ANTHILL.
""")


def main():
    parser = argparse.ArgumentParser(description="ANTHILL Core v1.4")
    parser.add_argument("--api", action="store_true", help="Start local FastAPI backend mode.")
    parser.add_argument("--cli", action="store_true", help="Start CLI mode explicitly.")
    args = parser.parse_args()

    if args.api:
        run_api_server()
        return

    print("[bold yellow]ANTHILL Core v1.4 online.[/bold yellow]")
    print("[dim]Type a mission for the Queen. Type '/help' for commands. Type 'exit' to quit.[/dim]")
    if USE_OLLAMA:
        print(f"[dim]Ollama mode: ON | Default Model: {OLLAMA_MODEL}[/dim]")
        print("[dim]Dynamic planning: ON[/dim]")
        print("[dim]LLM verifier: ON[/dim]")
    else:
        print("[dim]Ollama mode: OFF | Using fallback static builder, planner, coder, and verifier.[/dim]")
    print(f"[dim]File tools: {'ON' if ENABLE_FILE_TOOLS else 'OFF'}[/dim]")
    print(f"[dim]Shell tool: {'ON' if ENABLE_SHELL_TOOL else 'OFF'}[/dim]")
    print(f"[dim]Allowed workspace root: {resolve_workspace_root()}[/dim]")
    print(f"[dim]Parallel execution: {'ON' if ENABLE_PARALLEL_EXECUTION else 'OFF'} | Workers: {MAX_PARALLEL_WORKERS}[/dim]")
    print(f"[dim]Auto dependency wiring: {'ON' if ENABLE_AUTO_DEPENDENCY_WIRING else 'OFF'}[/dim]")
    print(f"[dim]FTS memory requested: {'ON' if ENABLE_FTS_MEMORY else 'OFF'}[/dim]")
    print("[dim]Event ledger: ON[/dim]")
    print("[dim]Pheromone trails v2: ON[/dim]")
    print("[dim]Structured patch proposals: ON[/dim]")
    print("[dim]Patch inspection commands: ON[/dim]")
    print("[dim]Approval gate: ON[/dim]")
    print("[dim]ApplyPatchTool: ON[/dim]")
    print("[dim]Framework alignment audit: ON[/dim]")
    print(f"[dim]Model routing: {'ON' if ENABLE_MODEL_ROUTING else 'OFF'}[/dim]")
    print(f"[dim]Context packets: {'ON' if ENABLE_CONTEXT_PACKETS else 'OFF'}[/dim]")
    print(f"[dim]Result summaries: {'ON' if ENABLE_RESULT_SUMMARIES else 'OFF'}[/dim]")
    print(f"[dim]Message metrics: {'ON' if ENABLE_MESSAGE_METRICS else 'OFF'}[/dim]")
    print(f"[dim]Task metrics: {'ON' if ENABLE_TASK_METRICS else 'OFF'} | Max task seconds: {MAX_TASK_SECONDS}[/dim]")
    print("[dim]Parallel safety: locked snapshots ON[/dim]")
    print("[dim]Memory visibility commands: ON[/dim]")
    print("[dim]Pheromone visibility commands: ON[/dim]")
    print("[dim]Event visibility commands: ON[/dim]")
    print("[dim]Runtime diagnostics: ON[/dim]")
    print(f"[dim]API backend: ON | Token auth: ON | Local: http://{API_HOST}:{API_PORT} | Start with --api[/dim]")
    print(f"[dim]External research: {'ON' if ENABLE_WEB_SEARCH else 'OFF'} | Provider: {WEB_SEARCH_PROVIDER}[/dim]")
    print(f"[dim]Source quality engine: ON | Source budget/mission: {MAX_SOURCES_PER_MISSION}[/dim]")
    print(f"[dim]Source allowlist domains: {len(SOURCE_ALLOWLIST_DOMAINS)} | blocklist domains: {len(SOURCE_BLOCKLIST_DOMAINS)}[/dim]")
    print(f"[dim]Patch application: {'ON' if ENABLE_PATCH_APPLICATION else 'OFF'}[/dim]")
    print(f"[dim]File writing: {'ON' if ENABLE_FILE_WRITING else 'OFF'}[/dim]\n")

    queen = Queen()

    while True:
        user_goal = input("Mission > ").strip()
        if user_goal.lower() in ["exit", "quit"]:
            print("[bold red]ANTHILL shutting down.[/bold red]")
            break
        if not user_goal:
            continue
        if user_goal == "/help":
            print_help()
            continue
        if user_goal == "/status":
            print(queen.format_system_status())
            continue
        if user_goal.startswith("/messages"):
            parts = user_goal.split()
            limit = 20
            if len(parts) > 1:
                try:
                    limit = int(parts[1])
                    limit = max(1, min(limit, 100))
                except ValueError:
                    print("[red]Message metric limit must be a number.[/red]")
                    continue
            print(queen.format_message_metrics(limit=limit))
            continue
        if user_goal.startswith("/tasks"):
            parts = user_goal.split()
            limit = 20
            if len(parts) > 1:
                try:
                    limit = int(parts[1])
                    limit = max(1, min(limit, 100))
                except ValueError:
                    print("[red]Task metric limit must be a number.[/red]")
                    continue
            print(queen.format_task_metrics(limit=limit))
            continue
        if user_goal.startswith("/source-quality"):
            parts = user_goal.split()
            limit = SOURCE_QUALITY_LIST_LIMIT_DEFAULT
            if len(parts) > 1:
                try:
                    limit = int(parts[1])
                    limit = max(1, min(limit, 50))
                except ValueError:
                    print("[red]Source quality limit must be a number.[/red]")
                    continue
            print(queen.format_source_quality(limit=limit))
            continue
        if user_goal.startswith("/sources"):
            parts = user_goal.split()
            limit = SOURCE_LIST_LIMIT_DEFAULT
            if len(parts) > 1:
                try:
                    limit = int(parts[1])
                    limit = max(1, min(limit, 50))
                except ValueError:
                    print("[red]Source list limit must be a number.[/red]")
                    continue
            print(queen.format_sources(limit=limit))
            continue

        if user_goal.startswith("/source "):
            source_id = user_goal.replace("/source ", "", 1).strip()
            print(queen.format_source_detail(source_id))
            continue

        if user_goal.startswith("/events"):
            parts = user_goal.split()
            limit = EVENT_LIST_LIMIT_DEFAULT
            event_type = None
            mission_id = None
            for part in parts[1:]:
                if part.startswith("type="):
                    event_type = part.replace("type=", "", 1).strip() or None
                elif part.startswith("mission="):
                    mission_id = part.replace("mission=", "", 1).strip() or None
                else:
                    try:
                        limit = max(1, min(int(part), 200))
                    except ValueError:
                        print("[red]Event command supports a number limit, type=<event_type>, and mission=<mission_id>.[/red]")
                        event_type = "__invalid__"
                        break
            if event_type == "__invalid__":
                continue
            print(queen.format_event_log(limit=limit, event_type=event_type, mission_id=mission_id))
            continue
        if user_goal == "/diagnostics":
            print(queen.format_runtime_diagnostics())
            continue
        if user_goal == "/models":
            print(queen.format_model_status())
            continue
        if user_goal == "/routes":
            print(queen.format_model_routes())
            continue
        if user_goal.startswith("/memory"):
            parts = user_goal.split()
            limit = MEMORY_LIST_LIMIT_DEFAULT
            if len(parts) > 1:
                try:
                    limit = int(parts[1])
                    limit = max(1, min(limit, 50))
                except ValueError:
                    print("[red]Memory limit must be a number.[/red]")
                    continue
            print(queen.format_memory_view(limit=limit))
            continue
        if user_goal.startswith("/pheromones"):
            parts = user_goal.split()
            limit = PHEROMONE_LIST_LIMIT_DEFAULT
            if len(parts) > 1:
                try:
                    limit = int(parts[1])
                    limit = max(1, min(limit, 50))
                except ValueError:
                    print("[red]Pheromone limit must be a number.[/red]")
                    continue
            print(queen.format_pheromone_view(limit=limit))
            continue
        if user_goal == "/approvals":
            print(queen.format_pending_approvals())
            continue
        if user_goal.startswith("/approval "):
            approval_id = user_goal.replace("/approval ", "", 1).strip()
            print(queen.format_approval_detail(approval_id))
            continue
        if user_goal.startswith("/approve "):
            approval_id = user_goal.replace("/approve ", "", 1).strip()
            print(queen.approve_request(approval_id))
            continue
        if user_goal.startswith("/reject "):
            remainder = user_goal.replace("/reject ", "", 1).strip()
            if not remainder:
                print("[red]Missing approval id.[/red]")
                continue
            parts = remainder.split(" ", 1)
            approval_id = parts[0].strip()
            reason = parts[1].strip() if len(parts) > 1 else None
            print(queen.reject_request(approval_id, reason=reason))
            continue
        if user_goal.startswith("/apply "):
            approval_id = user_goal.replace("/apply ", "", 1).strip()
            print(queen.apply_approved_patch(approval_id))
            continue
        if user_goal.startswith("/patches"):
            parts = user_goal.split()
            limit = PATCH_LIST_LIMIT_DEFAULT
            if len(parts) > 1:
                try:
                    limit = max(1, min(int(parts[1]), 50))
                except ValueError:
                    print("[red]Patch list limit must be a number.[/red]")
                    continue
            print(queen.format_patch_list(limit=limit))
            continue
        if user_goal.startswith("/patch "):
            patch_id = user_goal.replace("/patch ", "", 1).strip()
            print(queen.format_patch_detail(patch_id))
            continue
        if user_goal.startswith("/history"):
            parts = user_goal.split()
            limit = HISTORY_LIMIT_DEFAULT
            if len(parts) > 1:
                try:
                    limit = max(1, min(int(parts[1]), 50))
                except ValueError:
                    print("[red]History limit must be a number.[/red]")
                    continue
            print(queen.format_mission_history(limit=limit))
            continue
        if len(user_goal) > MAX_GOAL_LENGTH:
            print(f"[red]Mission too long. Please keep it under {MAX_GOAL_LENGTH} characters.[/red]")
            continue
        result = queen.run_mission(user_goal)
        print("\n[bold green]Final Result:[/bold green]")
        print(result)
        print("\n" + "-" * 60 + "\n")


if __name__ == "__main__":
    main()
