// v3.8.4 — the reasoning protocol moved from Anthill.Core.Models to Anthill.SDK.Reasoning.
// ApiHost consumes these types directly (the provider settings surface, the "Test Connection"
// action, the model-fitness report), so it picks them up here for the same reason Anthill.Core
// does: the move is a change of declaring assembly, not of meaning, and it should not read as a
// twenty-file edit.
global using Anthill.SDK.Reasoning;
