/* Colony Live 3D — topology + state projection.
   Deterministic. No rendering here: this module answers "what exists and what is true",
   colony-renderer.js answers "how it looks". Record shapes follow the repo:
   TaskContract (docs/CONTRACTS.md), ToolResult status/FailureClass, MoundRecord/FleetItem
   (Anthill.Modules.Micromound). Content is invented sample data. */

export function mulberry32(a) {
  return function () {
    a |= 0; a = a + 0x6D2B79F5 | 0;
    let t = Math.imul(a ^ a >>> 15, 1 | a);
    t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
    return ((t ^ t >>> 14) >>> 0) / 4294967296;
  };
}

/* Console tokens (src/Anthill.UI/index.html :root) reconciled with the concept image. */
export const TOKENS = {
  bg: '#05070c', ink: '#0a0e1a', panel: '#0d1320', card: '#101625', border: '#262e44',
  text: '#c9cfdc', muted: '#8b93a8', dim: '#667089', cream: '#f4e9d6',
  queen: '#ff3fa4', queenDeep: '#e21f7b', gold: '#f5b23c', goldHot: '#ffd98a',
  cyan: '#35aadf', cyanHot: '#57c7f0', orange: '#fb923c', rose: '#ef4444',
  amber: '#f59e0b', purple: '#8b5cf6', root: '#232a3a', rootLit: '#ff3fa4'
};

/* Stable spatial grammar. Queen sits left of centre; sector angles never re-solve. */
export const SECTORS = [
  {
    id: 'queen', label: "QUEEN'S CORE", pos: [0, 0, 0], r: 7.7, mass: 1.55,
    shell: TOKENS.queenDeep, core: TOKENS.gold, nucleus: '#c2247e', points: 2600,
    leads: ['Queen', 'Director', 'PlannerAnt', 'ConstraintAnt'],
    workers: ['MissionPlanner', 'DependencyMapper', 'ScopeGuard', 'ToolGuard'],
    clusters: ['Mission intake', 'Authority state', 'Recorded plans', 'Constraint rulings',
      'Directives', 'Objective ledger', 'Approval boundaries', 'Delegation grants', 'Mission roots'],
    labelSide: 'left'
  },
  {
    id: 'intelligence', label: 'INTELLIGENCE', pos: [-16.5, 0, 16.5], r: 5.0, mass: 1,
    shell: TOKENS.cyan, core: TOKENS.cyanHot, nucleus: '#0d7f9c', points: 1500,
    leads: ['ResearcherAnt', 'FileAnt', 'WebAnt', 'UICartographerAnt'],
    workers: ['RepoResearcher', 'MissionResearcher', 'RuntimeResearcher', 'FileScout', 'FileReader',
      'SourceFinder', 'SourceVerifier', 'RouteMapper', 'ComponentMapper'],
    clusters: ['Source scans', 'Web findings', 'Repo reads', 'Prior research', 'Open questions',
      'Contradictions', 'Citations', 'Discarded leads', 'Handoff briefs'],
    labelSide: 'right'
  },
  {
    id: 'forge', label: 'FORGE', pos: [16.5, 0, 16.5], r: 5.3, mass: 1.06,
    shell: TOKENS.orange, core: '#ffb26b', nucleus: '#c05f18', points: 1600,
    leads: ['CoderAnt', 'ScribeAnt'],
    workers: ['BackendCoder', 'UICoder', 'DocsCoder', 'ChangelogScribe', 'OperatorScribe'],
    clusters: ['Proposed patches', 'Build artifacts', 'Workspace reads', 'Test scaffolds',
      'Rejected diffs', 'Dependency notes', 'Idempotency keys', 'Compensations', 'Handoff briefs'],
    labelSide: 'right'
  },
  {
    id: 'validation', label: 'VALIDATION', pos: [16.5, 0, -16.5], r: 5.0, mass: 1,
    shell: TOKENS.rose, core: '#ff7a7a', nucleus: '#b02f3e', points: 1450,
    leads: ['VerifierAnt', 'TesterAnt', 'SoldierAnt', 'MedicAnt'],
    workers: ['ResultVerifier', 'SafetyVerifier', 'DotnetTester', 'FrontendTester', 'ActionProposer',
      'ExternalActionProposer', 'RuntimeSentinel', 'PatchSentinel', 'FailureDiagnoser', 'FixRouter'],
    clusters: ['Verification runs', 'Failure taxonomy', 'Evidence bundles', 'Risk rulings',
      'Approval requests', 'Regression guards', 'Refusals', 'Signed outcomes', 'Handoff briefs'],
    labelSide: 'right'
  },
  {
    id: 'memory', label: 'MEMORY', pos: [-16.5, 0, -16.5], r: 5.5, mass: 1.1,
    shell: TOKENS.amber, core: '#ffcf6b', nucleus: '#b8811c', points: 1750,
    leads: ['ArchivistAnt', 'ChangeArchivistAnt'],
    workers: ['MemoryArchivist', 'RuleArchivist'],
    clusters: ['Durable memories', 'Pheromone trails', 'Verified outcomes', 'Mission histories',
      'Evidence archive', 'Decayed signals', 'Reinforced routes', 'Failure lessons', 'Retrievals'],
    labelSide: 'right'
  },
  {
    id: 'output', label: 'OUTPUT', pos: [0, 17.0, 0], r: 4.7, mass: 0.95,
    shell: TOKENS.purple, core: '#b79bff', nucleus: '#6247c0', points: 1250,
    leads: ['BuilderAnt'],
    workers: ['ResponseBuilder', 'ResultCompiler'],
    clusters: ['Mission results', 'Operator briefs', 'Change summaries', 'Open decisions',
      'Delivered reports', 'Rendered diffs', 'Read receipts', 'Follow-ups', 'Retrievals'],
    labelSide: 'left'
  },
  {
    id: 'micromound', label: 'INFRASTRUCTURE\nMICROMOUND', pos: [0, -17.0, 0], r: 3.1,
    mass: 0.6, child: true,
    shell: TOKENS.queen, core: TOKENS.queen, nucleus: '#bc2872', points: 240,
    leads: ['QuartermasterAnt', 'InventoryAnt', 'ProxmoxAnt', 'StorageAnt', 'BackupAnt',
      'NetworkScoutAnt', 'HealthAnt', 'SecurityScoutAnt'],
    workers: ['ResourceMonitor', 'ConcurrencyAdvisor'],
    clusters: ['Evidence beats', 'Capabilities', 'Hardware profile', 'Sync chain'],
    labelSide: 'left'
  }
];

/* Operator overrides. Colour drives the whole chamber palette: shell as given,
   a lightened core for record tint and lead ants, a deepened nucleus for the core
   light. Names are free text; ids and topology never change. */
export function applyChamberConfig(cfg) {
  if (!cfg) return SECTORS;
  SECTORS.forEach(s => {
    const c = cfg[s.id]; if (!c) return;
    if (c.label) s.label = c.label;
  });
  return SECTORS;
}

export const SECTOR_BY_ID = SECTORS.reduce((m, s) => (m[s.id] = s, m), {});

/* Structural roots — permanent topology. `authority` is the single delegation root to the child. */
export const ROOTS = [
  { id: 'q-i', from: 'queen', to: 'intelligence', kind: 'structural', bow: [1.6, 0.6, -3.4] },
  { id: 'q-f', from: 'queen', to: 'forge', kind: 'structural', bow: [0.4, 2.2, 3.2] },
  { id: 'q-m', from: 'queen', to: 'memory', kind: 'structural', bow: [1.2, -1.4, 4.0] },
  { id: 'q-o', from: 'queen', to: 'output', kind: 'structural', bow: [-1.0, -2.4, -2.6] },
  { id: 'i-f', from: 'intelligence', to: 'forge', kind: 'structural', bow: [0.2, 2.8, -1.8] },
  { id: 'f-v', from: 'forge', to: 'validation', kind: 'structural', bow: [2.6, 0.4, 2.2] },
  { id: 'v-m', from: 'validation', to: 'memory', kind: 'structural', bow: [2.4, -1.0, -2.0] },
  { id: 'q-mm', from: 'queen', to: 'micromound', kind: 'authority', bow: [0.6, -1.2, 3.0] },
  /* lateral galleries: every chamber tunnels to every other chamber (the Micromound only to the Queen) */
  { id: 'q-v', from: 'queen', to: 'validation', kind: 'lateral', bow: [0.5, 4.2, -1.6] },
  { id: 'i-v', from: 'intelligence', to: 'validation', kind: 'lateral', bow: [2.0, 2.6, 2.4] },
  { id: 'i-m', from: 'intelligence', to: 'memory', kind: 'lateral', bow: [3.2, 1.0, -2.8] },
  { id: 'i-o', from: 'intelligence', to: 'output', kind: 'lateral', bow: [-2.8, 1.4, 2.0] },
  { id: 'f-m', from: 'forge', to: 'memory', kind: 'lateral', bow: [2.8, -0.6, 2.6] },
  { id: 'f-o', from: 'forge', to: 'output', kind: 'lateral', bow: [-1.2, 3.4, -2.2] },
  { id: 'v-o', from: 'validation', to: 'output', kind: 'lateral', bow: [0.8, -3.6, -2.4] },
  { id: 'm-o', from: 'memory', to: 'output', kind: 'lateral', bow: [-0.6, -3.8, 2.2] }
];

/* The illuminated circuit for the sample mission, in dispatch order. */
export const CIRCUIT = ['q-i', 'i-f', 'f-v', 'v-m', 'q-o'];

/* ---- The sample mission -------------------------------------------------------------------
   Repo-shaped: each step projects to a TaskContract (task_type / side_effect_class /
   risk_class / required_capabilities) exactly as ContractGate.Admit would see it. */
export const MISSION = {
  id: 'msn_7f31c4',
  title: 'Nightly Proxmox backup verification has been failing since 08-27',
  progress: [4, 7],
  state: 'active',
  steps: [
    {
      n: 1, sector: 'queen', segment: null, ant: 'Planner', dur: 4.5,
      task: 'tsk_01', title: 'Admit mission and record the plan', task_type: 'diagnose',
      caps: ['mission.plan'], side_effect: 'none', risk: 'low',
      note: 'Queen admits the mission; Planner records a five-task plan. Constraint attaches the destructive-action ceiling.',
      creates: [{ sector: 'queen', cluster: 'Recorded plans', n: 3, type: 'plan' }]
    },
    {
      n: 2, sector: 'intelligence', segment: 'q-i', ant: 'Researcher', dur: 8,
      task: 'tsk_02', title: 'Read backup job history and mound evidence beats', task_type: 'research',
      caps: ['repo.read', 'network.http.public', 'micromound.read'], side_effect: 'none', risk: 'low',
      note: 'Researcher enters Intelligence. Nineteen context points form on the outer shell; four prior memories connect from deeper in the sphere.',
      creates: [{ sector: 'intelligence', cluster: 'Source scans', n: 19, type: 'context' },
      { sector: 'intelligence', cluster: 'Prior research', n: 4, type: 'memory' }]
    },
    {
      n: 3, sector: 'forge', segment: 'i-f', ant: 'Coder', dur: 9,
      task: 'tsk_03', title: 'Propose a retry-window patch to the verification job', task_type: 'change',
      caps: ['repo.read', 'repo.patch.propose'], side_effect: 'reversible', risk: 'medium',
      note: 'Handoff brief moves through the Intelligence→Forge root. Coder creates one artifact cluster: a proposed patch, its idempotency key, and a compensation note.',
      creates: [{ sector: 'forge', cluster: 'Proposed patches', n: 6, type: 'artifact' },
      { sector: 'forge', cluster: 'Compensations', n: 2, type: 'artifact' }]
    },
    {
      n: 4, sector: 'validation', segment: 'f-v', ant: 'Verifier', dur: 9,
      task: 'tsk_04', title: 'Verify the patch against mound evidence', task_type: 'verify',
      caps: ['repo.read', 'build.run', 'micromound.read'], side_effect: 'none', risk: 'medium',
      note: 'Verifier checks the artifact against seven evidence beats returned by the Micromound. One regression guard is added.',
      creates: [{ sector: 'validation', cluster: 'Verification runs', n: 8, type: 'evidence' },
      { sector: 'validation', cluster: 'Regression guards', n: 1, type: 'evidence' }]
    },
    {
      n: 5, sector: 'validation', segment: 'v-m', ant: 'Verifier', dur: 7, approval: true,
      task: 'tsk_05', title: 'Apply the patch to the verification job', task_type: 'change',
      caps: ['repo.patch.apply'], side_effect: 'destructive', risk: 'high',
      note: 'The active root halts at the approval boundary. Nothing advances until an operator decides. Stop always wins.',
      creates: []
    },
    {
      n: 6, sector: 'memory', segment: 'v-m', ant: 'Archivist', dur: 8,
      task: 'tsk_06', title: 'Settle verified evidence into durable memory', task_type: 'change',
      caps: ['memory.write'], side_effect: 'reversible', risk: 'low',
      note: 'Durable evidence travels toward Memory. The verified relationship settles inward toward the core; the pheromone trail on the Forge→Validation root strengthens.',
      creates: [{ sector: 'memory', cluster: 'Durable memories', n: 5, type: 'memory' },
      { sector: 'memory', cluster: 'Reinforced routes', n: 2, type: 'pheromone' }]
    },
    {
      n: 7, sector: 'output', segment: 'q-o', ant: 'Operator', dur: 6,
      task: 'tsk_07', title: 'Deliver the operator-facing result', task_type: 'change',
      caps: ['ui.render'], side_effect: 'none', risk: 'low',
      note: 'Output receives the result. The mission route dims into a persistent pheromone trail.',
      creates: [{ sector: 'output', cluster: 'Mission results', n: 3, type: 'result' },
      { sector: 'output', cluster: 'Change summaries', n: 2, type: 'result' }]
    }
  ]
};

/* ---- Micromound facts renderable today (M1, read-only) ---- */
export const MOUND = {
  mound_id: 'mm-rack-01', name: 'Rack Micromound', controller: 'Mound Major',
  protocol_value: 'edge_queen', tier: 'standard', status: 'online', enrolled: true,
  last_seen: '2026-09-01T11:22:07Z', last_seq: 4182, sync_interval_s: 30,
  capabilities: ['proxmox.vm.read', 'storage.pool.read', 'backup.job.read', 'net.probe', 'health.report'],
  hardware: 'x86_64 · 8c/16t · 64 GB · 2×2 TB NVMe',
  beats: [
    { seq: 4182, state: 'accepted', envelopes: 3, received_at: '11:22:07' },
    { seq: 4181, state: 'accepted', envelopes: 2, received_at: '11:21:37' },
    { seq: 4180, state: 'accepted', envelopes: 4, received_at: '11:21:07' },
    { seq: 4179, state: 'refused', envelopes: 1, received_at: '11:20:37', reason: 'chain digest did not continue' }
  ],
  global_stop: false, per_mound_stop: false, chain_health: 'continuous since seq 4180'
};

/* ---- Runtime state vocabulary — one source for ring colour, chip and inspector ---- */
export const STATES = {
  idle: { label: 'IDLE', color: TOKENS.dim, note: 'Structure visible, nothing illuminated.' },
  active: { label: 'ACTIVE', color: TOKENS.queen, note: 'One route illuminated, ants moving.' },
  waiting: { label: 'WAITING', color: TOKENS.amber, note: 'Route lit but static; ant docked at boundary.' },
  blocked: { label: 'BLOCKED', color: TOKENS.rose, note: 'Route breaks at the failure point.' },
  approval: { label: 'NEEDS YOU', color: TOKENS.queen, note: 'Route halts at the approval boundary.' },
  complete: { label: 'COMPLETE', color: '#10b981', note: 'Route dims to a pheromone trail.' },
  failed: { label: 'FAILED', color: TOKENS.rose, note: 'Branch drifts outward and dims.' },
  degraded: { label: 'DEGRADED', color: TOKENS.orange, note: 'Reduced point budget, flow simplified.' },
  disconnected: { label: 'DISCONNECTED', color: TOKENS.dim, note: 'Child sphere desaturates; grants no authority.' },
  stopped: { label: 'STOPPED', color: TOKENS.rose, note: 'Authority root severed and capped.' },
  incompatible: { label: 'INCOMPATIBLE', color: TOKENS.muted, note: 'Child outlined, never illuminated.' }
};

const TYPE_TINT = {
  context: 0.62, plan: 0.9, task: 0.7, artifact: 0.8, evidence: 0.95,
  memory: 1.0, decision: 0.9, result: 0.75, failure: 0.35, pheromone: 0.85
};

export const rosterOf = s => (s.leads || []).concat(s.workers || []);
const VERIF = ['verified', 'verified', 'unverified', 'verified', 'refused'];
const TYPES_FOR = {
  queen: ['plan', 'decision', 'task', 'context'],
  intelligence: ['context', 'evidence', 'memory', 'failure'],
  forge: ['artifact', 'task', 'context', 'failure'],
  validation: ['evidence', 'decision', 'artifact', 'failure'],
  memory: ['memory', 'pheromone', 'evidence', 'decision'],
  output: ['result', 'context', 'decision'],
  micromound: ['evidence', 'context']
};

/* Deterministic context structure for one sector: clusters placed on a stable lattice,
   records placed inside their cluster. Depth = durability: verified/high-pheromone records
   sit inward, weak and failed ones drift toward the shell. */
export function buildContext(sector) {
  const rnd = mulberry32(hash(sector.id));
  const types = TYPES_FOR[sector.id] || ['context'];
  const clusters = sector.clusters.map((label, i) => {
    const n = sector.clusters.length;
    const golden = Math.PI * (3 - Math.sqrt(5));
    const y = 1 - (i / Math.max(1, n - 1)) * 1.55;
    const rad = Math.sqrt(Math.max(0.05, 1 - y * y));
    const th = golden * i;
    const shellFrac = 0.42 + rnd() * 0.36;
    const center = [
      Math.cos(th) * rad * sector.r * shellFrac,
      y * sector.r * shellFrac * 0.85,
      Math.sin(th) * rad * sector.r * shellFrac
    ];
    const count = 6 + Math.floor(rnd() * 12);
    const records = [];
    for (let k = 0; k < count; k++) {
      const type = types[Math.floor(rnd() * types.length)];
      const pher = Math.round((type === 'memory' ? 0.62 : 0.18) * 100 + rnd() * 38) / 100;
      const verification = type === 'failure' ? 'refused' : VERIF[Math.floor(rnd() * VERIF.length)];
      const durable = (verification === 'verified' ? 0.55 : 0.1) + pher * 0.45;
      const depth = 1 - Math.min(0.94, durable);
      const dir = [rnd() * 2 - 1, rnd() * 2 - 1, rnd() * 2 - 1];
      const len = Math.hypot(dir[0], dir[1], dir[2]) || 1;
      const spread = sector.r * 0.16;
      records.push({
        id: sector.id.slice(0, 3) + '_' + label.toLowerCase().replace(/[^a-z]/g, '').slice(0, 4) + '_' + k,
        title: recordTitle(label, type, k, rnd),
        type, cluster: label, sector: sector.id,
        ant: (() => { const rr = rosterOf(sector); return rr[Math.floor(rnd() * rr.length)] || 'Worker'; })(),
        mission: rnd() > 0.55 ? MISSION.id : 'msn_' + (0x1000 + Math.floor(rnd() * 0xefff)).toString(16),
        ts: stamp(rnd), verification, pheromone: Math.min(0.99, pher),
        tint: TYPE_TINT[type] || 0.6,
        depth,
        pos: [
          center[0] * (0.5 + depth * 0.75) + (dir[0] / len) * spread,
          center[1] * (0.5 + depth * 0.75) + (dir[1] / len) * spread,
          center[2] * (0.5 + depth * 0.75) + (dir[2] / len) * spread
        ],
        links: 1 + Math.floor(rnd() * 5)
      });
    }
    return { id: sector.id + '_c' + i, label, center, records };
  });
  return { clusters };
}

function recordTitle(cluster, type, k, rnd) {
  const S = {
    plan: ['Five-task plan for msn_7f31c4', 'Replan after verification refusal', 'Task ordering with dependency tsk_03'],
    decision: ['Destructive ceiling set to reversible', 'Approval required for repo.patch.apply', 'Retry refused: failure not transient'],
    task: ['tsk_04 verify patch against evidence', 'tsk_02 read backup job history', 'tsk_06 settle evidence into memory'],
    context: ['backup-verify.service unit read', 'Job log window 08-27 → 08-31', 'Mound beat seq 4180 envelope 2', 'Retry window discussion'],
    artifact: ['patch: widen retry window to 15m', 'build output dotnet test 214 passed', 'compensation: revert unit file'],
    evidence: ['verification run 214/214 passed', 'evidence bundle ev_9c2 signed', 'beat 4179 refused — chain digest', 'guard: UiIntegrity holds'],
    memory: ['Nightly verify fails under NVMe contention', 'Retry window 15m holds across 6 runs', 'Proxmox 8.2 backup lock behaviour'],
    pheromone: ['Forge→Validation route reinforced ×6', 'Intelligence→Forge handoff strength 0.71'],
    result: ['Operator brief: backup verification restored', 'Change summary for 1 applied patch'],
    failure: ['failed_permanent: authorization', 'discarded lead: NFS timeout theory']
  };
  const arr = S[type] || S.context;
  return arr[Math.floor(rnd() * arr.length)];
}

function stamp(rnd) {
  const h = 8 + Math.floor(rnd() * 4), m = Math.floor(rnd() * 60);
  return '2026-09-01 ' + String(h).padStart(2, '0') + ':' + String(m).padStart(2, '0');
}

function hash(s) { let h = 2166136261; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); } return h >>> 0; }

export const CONTEXT = SECTORS.reduce((m, s) => (m[s.id] = buildContext(s), m), {});

/* One telemetry record per child colony. MOUND stays exported as the rack mound so
   existing call sites keep working. */
export const MOUNDS = {
  micromound: MOUND
};
