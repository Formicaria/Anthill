# ============================================================
#  ANTHILL - Single File Version
#  Run with: python anthill.py
#  Requirements: pip install pydantic rich
# ============================================================

from rich import print
from abc import ABC, abstractmethod
from enum import Enum
from typing import List, Optional
from uuid import uuid4
from datetime import datetime
from pydantic import BaseModel, Field
import sqlite3
from pathlib import Path


# ============================================================
#  MODELS  (mission.py)
# ============================================================

class TaskStatus(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    COMPLETE = "complete"
    FAILED = "failed"


class MissionStatus(str, Enum):
    CREATED = "created"
    RUNNING = "running"
    COMPLETE = "complete"
    FAILED = "failed"


class Task(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    title: str
    description: str
    assigned_ant: str
    status: TaskStatus = TaskStatus.PENDING
    result: Optional[str] = None


class Mission(BaseModel):
    id: str = Field(default_factory=lambda: str(uuid4()))
    goal: str
    tasks: List[Task] = []
    status: MissionStatus = MissionStatus.CREATED
    final_result: Optional[str] = None
    created_at: datetime = Field(default_factory=datetime.utcnow)


# ============================================================
#  PLANNER  (planner.py)
# ============================================================

class Planner:
    def create_tasks(self, goal: str):
        """
        Stage 1 planner.

        For now, every mission gets the same basic 3-step pipeline:
        research, build, verify.

        Later, this will be replaced with Ollama/local LLM planning.
        """
        return [
            Task(
                title="Research mission",
                description=f"Understand the goal and gather useful context: {goal}",
                assigned_ant="researcher",
            ),
            Task(
                title="Build response",
                description=f"Create a practical answer or action plan for: {goal}",
                assigned_ant="builder",
            ),
            Task(
                title="Verify result",
                description=f"Check the result for accuracy, usefulness, and missing steps: {goal}",
                assigned_ant="verifier",
            ),
        ]


# ============================================================
#  ANTS  (base_ant.py, researcher_ant.py, builder_ant.py, verifier_ant.py)
# ============================================================

class BaseAnt(ABC):
    def __init__(self, name: str):
        self.name = name

    @abstractmethod
    def run(self, task: Task, mission: Mission) -> str:
        pass


class ResearcherAnt(BaseAnt):
    def __init__(self):
        super().__init__(name="researcher")

    def run(self, task: Task, mission: Mission) -> str:
        return (
            f"Researcher Ant analyzed the mission goal: '{mission.goal}'. "
            f"The mission requires breaking the request into clear steps, "
            f"producing a useful result, and saving the outcome to memory."
        )


class BuilderAnt(BaseAnt):
    def __init__(self):
        super().__init__(name="builder")

    def run(self, task: Task, mission: Mission) -> str:
        research_results = [
            t.result for t in mission.tasks
            if t.assigned_ant == "researcher" and t.result
        ]

        research_context = "\n".join(research_results) if research_results else "No research context available."

        return (
            f"Builder Ant created a basic mission response.\n\n"
            f"Mission Goal:\n{mission.goal}\n\n"
            f"Research Context:\n{research_context}\n\n"
            f"Proposed Output:\n"
            f"1. Understand the user's mission.\n"
            f"2. Break it into smaller tasks.\n"
            f"3. Assign each task to a specialized ant.\n"
            f"4. Collect results.\n"
            f"5. Verify the final answer.\n"
            f"6. Save the mission to memory."
        )


class VerifierAnt(BaseAnt):
    def __init__(self):
        super().__init__(name="verifier")

    def run(self, task: Task, mission: Mission) -> str:
        completed_tasks = [t for t in mission.tasks if t.result]

        if len(completed_tasks) >= 2:
            return (
                "Verifier Ant checked the mission. "
                "The mission has enough completed task output to return a final response."
            )

        return (
            "Verifier Ant found that the mission may be incomplete. "
            "More task output is needed before finalizing."
        )


# ============================================================
#  MEMORY  (sqlite_memory.py)
# ============================================================

class SQLiteMemory:
    def __init__(self, db_path: str = "data/anthill_memory.db"):
        self.db_path = db_path
        Path("data").mkdir(exist_ok=True)
        self._init_db()

    def _connect(self):
        return sqlite3.connect(self.db_path)

    def _init_db(self):
        with self._connect() as conn:
            cursor = conn.cursor()

            cursor.execute("""
                CREATE TABLE IF NOT EXISTS missions (
                    id TEXT PRIMARY KEY,
                    goal TEXT NOT NULL,
                    status TEXT NOT NULL,
                    final_result TEXT,
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
                    status TEXT NOT NULL,
                    result TEXT,
                    FOREIGN KEY (mission_id) REFERENCES missions(id)
                )
            """)

            conn.commit()

    def save_mission(self, mission: Mission):
        with self._connect() as conn:
            cursor = conn.cursor()

            cursor.execute("""
                INSERT OR REPLACE INTO missions (
                    id, goal, status, final_result, created_at, saved_at
                )
                VALUES (?, ?, ?, ?, ?, ?)
            """, (
                mission.id,
                mission.goal,
                mission.status.value,
                mission.final_result,
                mission.created_at.isoformat(),
                datetime.utcnow().isoformat(),
            ))

            for task in mission.tasks:
                cursor.execute("""
                    INSERT OR REPLACE INTO tasks (
                        id, mission_id, title, description, assigned_ant, status, result
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                """, (
                    task.id,
                    mission.id,
                    task.title,
                    task.description,
                    task.assigned_ant,
                    task.status.value,
                    task.result,
                ))

            conn.commit()


# ============================================================
#  QUEEN  (queen.py)
# ============================================================

class Queen:
    def __init__(self):
        self.planner = Planner()
        self.memory = SQLiteMemory()

        self.ants = {
            "researcher": ResearcherAnt(),
            "builder": BuilderAnt(),
            "verifier": VerifierAnt(),
        }

    def run_mission(self, goal: str) -> str:
        print(f"[bold cyan]Queen received mission:[/bold cyan] {goal}")

        mission = Mission(goal=goal)
        mission.status = MissionStatus.RUNNING

        mission.tasks = self.planner.create_tasks(goal)

        print(f"[dim]Mission ID: {mission.id}[/dim]")
        print(f"[dim]Created {len(mission.tasks)} tasks.[/dim]\n")

        for task in mission.tasks:
            print(f"[yellow]Assigning task to {task.assigned_ant} ant:[/yellow] {task.title}")

            ant = self.ants.get(task.assigned_ant)

            if not ant:
                task.status = TaskStatus.FAILED
                task.result = f"No ant found for role: {task.assigned_ant}"
                print(f"[red]{task.result}[/red]")
                continue

            try:
                task.status = TaskStatus.RUNNING
                task.result = ant.run(task, mission)
                task.status = TaskStatus.COMPLETE

                print(f"[green]Task complete:[/green] {task.title}")

            except Exception as error:
                task.status = TaskStatus.FAILED
                task.result = f"Task failed with error: {error}"
                print(f"[red]{task.result}[/red]")

        mission.final_result = self._compose_final_result(mission)
        mission.status = MissionStatus.COMPLETE

        self.memory.save_mission(mission)

        print("[bold magenta]Mission saved to ANTHILL memory.[/bold magenta]")

        return mission.final_result

    def _compose_final_result(self, mission: Mission) -> str:
        task_outputs = []

        for task in mission.tasks:
            task_outputs.append(
                f"Task: {task.title}\n"
                f"Ant: {task.assigned_ant}\n"
                f"Status: {task.status.value}\n"
                f"Result: {task.result}\n"
            )

        return (
            f"Mission Complete\n\n"
            f"Goal:\n{mission.goal}\n\n"
            f"Task Results:\n\n"
            + "\n".join(task_outputs)
        )


# ============================================================
#  MAIN ENTRY POINT
# ============================================================

def main():
    print("[bold yellow]ANTHILL Core online.[/bold yellow]")
    print("[dim]Type a mission for the Queen. Type 'exit' to quit.[/dim]\n")

    queen = Queen()

    while True:
        user_goal = input("Mission > ").strip()

        if user_goal.lower() in ["exit", "quit"]:
            print("[bold red]ANTHILL shutting down.[/bold red]")
            break

        if not user_goal:
            continue

        result = queen.run_mission(user_goal)

        print("\n[bold green]Final Result:[/bold green]")
        print(result)
        print("\n" + "-" * 60 + "\n")


if __name__ == "__main__":
    main()
