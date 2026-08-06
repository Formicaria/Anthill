// v3.8.7 — every `using Anthill.Core.*` in this module was deleted, and this file is what replaced
// the ones that still mattered.
//
// That deletion IS the phase. A module that imports the core is not a module, so the check is
// mechanical rather than a matter of judgement: if this project ever needs a type from
// Anthill.Core, either the type belongs in Anthill.SDK or the module is reaching for coordination
// it should not have. Applying that test moved five things — AnthillTime, Json, RiskLevel, the
// action vocabulary, and NullEventBus — and turned two more into contracts (IFieldCipher,
// HomelabOptions).
global using Anthill.SDK.Actions;
global using Anthill.SDK.Common;
global using Anthill.SDK.Events;
global using Anthill.SDK.Security;
