using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anthill.Core.Configuration;

/// <summary>
/// How exposed a setting is to an operator at runtime. v0.3.8.114, R0's fourth exit-gate clause.
/// </summary>
public enum ConfigExposure
{
    /// <summary>Editable only by hand-editing the file and restarting the host.</summary>
    FileOnly,

    /// <summary>Writable live through the settings surface under <c>manage_settings</c>.</summary>
    Editable,
}

/// <summary>
/// What a setting is worth to somebody who should not have it. Not an access rule — the API's
/// permissions are that — but a fact the generator needs, because a secret must never be written
/// into a generated example file with a plausible-looking value beside it.
/// </summary>
public enum ConfigSecurity
{
    /// <summary>No particular sensitivity.</summary>
    Ordinary,

    /// <summary>Names a host, port, path or endpoint. Not a secret; still environment-specific.</summary>
    Environment,

    /// <summary>Holds or names a credential. Never rendered with a value into generated artifacts.</summary>
    Secret,

    /// <summary>Changing it changes what the colony is permitted to do.</summary>
    Safety,
}

/// <summary>
/// THE FACTS A TYPE CANNOT STATE ABOUT ITSELF. v0.3.8.114.
///
/// The catalog reads key, CLR type and default straight off <see cref="AnthillConfig"/> — the
/// compiler already knows those, and `docs/GUARDS.md` puts compiled inspection above anything that
/// re-declares what the compiler holds. This attribute carries only the rest: whether an operator
/// can change it without a restart, what it is worth to an attacker, which environment variable
/// overrides it, what range is legal, and one line of prose for the generated documentation.
///
/// A property with no attribute is `FileOnly`, `Ordinary`, no override, no range — which is the
/// conservative reading in every direction, and the reason adding a setting cannot accidentally
/// publish it to the settings surface.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ConfigKeyAttribute : Attribute
{
    /// <summary>One line, for the generated documentation and the settings surface.</summary>
    public string Summary { get; init; } = "";

    public ConfigExposure Exposure { get; init; } = ConfigExposure.FileOnly;

    public ConfigSecurity Security { get; init; } = ConfigSecurity.Ordinary;

    /// <summary>Environment variable that overrides this key, or empty for none.</summary>
    public string EnvOverride { get; init; } = "";

    /// <summary>Inclusive numeric bounds. Both <see cref="double.NaN"/> when unbounded.</summary>
    public double Min { get; init; } = double.NaN;

    public double Max { get; init; } = double.NaN;

    /// <summary>
    /// Former spellings this key has had. The migration reads these; the docs render them so an
    /// operator following an old guide can find out their key was renamed rather than ignored.
    /// </summary>
    public string[] Aliases { get; init; } = [];

    /// <summary>
    /// Why this key is deliberately absent from the generated example file. Empty means it is
    /// documented. A non-empty reason is the successor to `ConfigurationSurfaceTests`' hand-kept
    /// `DeliberatelyUndocumented` ledger — moved onto the declaration so it cannot drift from the
    /// property it describes.
    /// </summary>
    public string UndocumentedBecause { get; init; } = "";

    /// <summary>
    /// When set, this key OPENS a section of the example file, and <see cref="SectionNote"/> is
    /// emitted as `_comment_&lt;Section&gt;` immediately above it.
    ///
    /// THE COMMENTS ARE DECLARATIONS, and that is the point. `config.example.json` carried
    /// twenty-four curated operator notes — several of them recording corrections that cost a
    /// release to find, like the roster flags that "looked like off switches and were not" and the
    /// api-token fallback that was itself. A generator that rendered keys and defaults faithfully
    /// while dropping those would satisfy this clause and leave the artifact worse than it found
    /// it, which is the shape this repository calls a check answering an adjacent question. So the
    /// prose moves into the catalog rather than being regenerated away.
    /// </summary>
    public string Section { get; init; } = "";

    /// <summary>The operator-facing note for the section this key opens. Rendered verbatim.</summary>
    public string SectionNote { get; init; } = "";

    /// <summary>
    /// An ILLUSTRATIVE value for `config.example.json`, as raw JSON, when showing the shipped
    /// default would teach an operator nothing. Empty means render the default.
    ///
    /// THE EXAMPLE FILE IS NOT A DUMP OF THE DEFAULTS, and v0.3.8.114 discovered that by predicting
    /// a two-line regeneration diff and getting a hundred-and-forty-eight-line one. `model_routes`
    /// defaults to an empty dictionary and the example showed four populated routes with real model
    /// names; `agent_workspace_dir` showed `/path/to/your/project`. Rendering defaults over those
    /// would have replaced working illustrations with `{}` — the same shape as dropping the section
    /// comments, and caught the same way.
    ///
    /// It is deliberately NOT used to show a safety gate enabled. See the class remarks.
    /// </summary>
    public string ExampleJson { get; init; } = "";
}

/// <summary>One settings key, as the catalog knows it.</summary>
public sealed record ConfigDeclaration(
    string Key,
    Type ClrType,
    object? Default,
    string Summary,
    ConfigExposure Exposure,
    ConfigSecurity Security,
    string EnvOverride,
    double Min,
    double Max,
    IReadOnlyList<string> Aliases,
    string UndocumentedBecause,
    string Section,
    string SectionNote,
    string ExampleJson)
{
    public bool IsDocumented => string.IsNullOrEmpty(UndocumentedBecause);

    public bool IsEditable => Exposure == ConfigExposure.Editable;

    public bool HasRange => !double.IsNaN(Min) || !double.IsNaN(Max);

    /// <summary>
    /// The rendered type name an operator reads in the docs — `string`, `int`, `bool`, `string[]`,
    /// `object`. Deliberately the JSON reader's vocabulary rather than C#'s: the file is JSON and
    /// the person editing it is not necessarily holding this repository.
    /// </summary>
    public string JsonType => JsonTypeOf(ClrType);

    /// <summary>
    /// What the example file shows: the declared illustration when there is one, the shipped default
    /// otherwise — and never a secret's value.
    /// </summary>
    public string RenderedJson(JsonSerializerOptions options)
    {
        // A SECRET BLANKS UNCONDITIONALLY, illustration or not. The first draft let a declared
        // ExampleJson through for a Secret key, which is a hole rather than a convenience: the whole
        // point of the classification is that nothing renders a value for these, and "unless someone
        // declared one" is exactly the exception an attacker or an absent-minded afternoon needs. If
        // a key genuinely wants to show a shape, it is not a secret and should not be classed one.
        if (Security == ConfigSecurity.Secret) return "\"\"";

        if (string.IsNullOrEmpty(ExampleJson)) return JsonSerializer.Serialize(Default, options);

        // The declaration holds COMPACT json — a C# attribute argument is a single literal, and a
        // multi-line raw string whose content begins with a quote needs a delimiter longer than the
        // content, which is a counting exercise nobody should have to get right by hand. So the
        // declaration carries data and the renderer carries presentation: an object or an array is
        // expanded here, where the reader is, rather than there, where the compiler is.
        var trimmed = ExampleJson.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            using var parsed = JsonDocument.Parse(ExampleJson);
            return JsonSerializer.Serialize(parsed.RootElement, IndentedOptions);
        }

        return ExampleJson;
    }

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    internal static string JsonTypeOf(Type type)
    {
        var inner = Nullable.GetUnderlyingType(type) ?? type;

        if (inner == typeof(string)) return "string";
        if (inner == typeof(bool)) return "bool";
        if (inner == typeof(int) || inner == typeof(long)) return "int";
        if (inner == typeof(double) || inner == typeof(float) || inner == typeof(decimal)) return "number";
        if (inner.IsEnum) return "string";

        if (inner.IsArray) return JsonTypeOf(inner.GetElementType()!) + "[]";

        if (inner.IsGenericType)
        {
            var definition = inner.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(IReadOnlyList<>))
                return JsonTypeOf(inner.GetGenericArguments()[0]) + "[]";
            if (definition == typeof(Dictionary<,>))
                return "object";
        }

        return "object";
    }
}

/// <summary>
/// THE CONFIGURATION SURFACE HAS ONE AUTHORITY. v0.3.8.114 — R0's fourth exit-gate clause, and the
/// last item standing between R0 and closed.
///
/// WHAT WAS WRONG. One fact — "this is a setting, of this type, with this default, and an operator
/// may or may not change it live" — was written down in four places that could disagree:
/// <see cref="AnthillConfig"/>'s property, `config.example.json`'s entry, `AnthillRuntime`'s
/// hand-kept `EditableConfigKeys` set, and the prose in `docs/`. v0.3.8.91 stopped that drift
/// getting worse by pinning the first two against each other with an explicit
/// deliberately-undocumented ledger, and said plainly what it had not done: "the GENERATED schema
/// is not built ... that is the end state; this release stopped the drift getting worse."
///
/// This is that generator. Defect class 5b — two stores of one fact, which cannot disagree loudly
/// because neither can see the other — closed for the surface an operator actually edits.
///
/// WHAT IS DERIVED AND WHAT IS DECLARED, because the split is the whole design. Key, CLR type and
/// default come off the type by reflection: the compiler holds them, `docs/GUARDS.md` ranks
/// compiled inspection above re-declaration, and a renamed property therefore cannot leave a stale
/// entry behind — it stops existing. Everything a type cannot state — live-editability, security
/// class, environment override, range, aliases, prose — is on
/// <see cref="ConfigKeyAttribute"/>, attached to the property it describes so the two move together.
///
/// The example file and the documentation are RENDERED from this, and a test regenerates both and
/// fails on any difference. `EditableConfigKeys` is a projection over it rather than a second list.
/// </summary>
public static class ConfigCatalog
{
    private static readonly Lazy<IReadOnlyList<ConfigDeclaration>> Lazy = new(Build);

    /// <summary>Every settings key, in declaration order — which is the order the file renders in.</summary>
    public static IReadOnlyList<ConfigDeclaration> Declarations => Lazy.Value;

    public static ConfigDeclaration? Find(string key) =>
        Declarations.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    private static readonly Lazy<HashSet<string>> LazyEditable = new(() =>
        Declarations.Where(d => d.IsEditable)
            .Select(d => d.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The keys the settings surface may write. THE one authority — `AnthillRuntime` projects this
    /// rather than keeping a parallel set, so a new editable setting is one attribute, not two edits
    /// that a reviewer has to notice are both required.
    ///
    /// Materialised once: <c>ApplySettingsUpdate</c> probes it per key in a loop, and rebuilding the
    /// set on each probe would make an operator's settings save quadratic in the size of the surface
    /// it is saving.
    /// </summary>
    public static IReadOnlyCollection<string> EditableKeys => LazyEditable.Value;

    /// <summary>Whether the settings surface may write this key. Ordinal-insensitive, as the set is.</summary>
    public static bool IsEditable(string key) => LazyEditable.Value.Contains(key);

    private static IReadOnlyList<ConfigDeclaration> Build()
    {
        // A fresh instance IS the default. Reading the initializer any other way — parsing the
        // source, or restating the value in the attribute — is the second store this class exists
        // to remove, and it would be wrong the first time somebody changed an initializer.
        var defaults = new AnthillConfig();

        return typeof(AnthillConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .Select(p =>
            {
                var name = p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name;
                var declared = p.GetCustomAttribute<ConfigKeyAttribute>();

                return new ConfigDeclaration(
                    Key: name,
                    ClrType: p.PropertyType,
                    Default: p.GetValue(defaults),
                    Summary: declared?.Summary ?? "",
                    Exposure: declared?.Exposure ?? ConfigExposure.FileOnly,
                    Security: declared?.Security ?? ConfigSecurity.Ordinary,
                    EnvOverride: declared?.EnvOverride ?? "",
                    Min: declared?.Min ?? double.NaN,
                    Max: declared?.Max ?? double.NaN,
                    Aliases: declared?.Aliases ?? [],
                    UndocumentedBecause: declared?.UndocumentedBecause ?? "",
                    Section: declared?.Section ?? "",
                    SectionNote: declared?.SectionNote ?? "",
                    ExampleJson: declared?.ExampleJson ?? "");
            })
            .ToList();
    }

    /// <summary>
    /// Render `config.example.json`. Undocumented keys are omitted by their declared reason, and a
    /// <see cref="ConfigSecurity.Secret"/> key renders its name with an empty value — an example
    /// file carrying a plausible-looking credential is how a placeholder ends up in production.
    /// </summary>
    public static string RenderExampleJson()
    {
        var body = new StringBuilder();
        body.Append("{\n");

        var documented = Declarations.Where(d => d.IsDocumented).ToList();
        for (var i = 0; i < documented.Count; i++)
        {
            var declaration = documented[i];
            var value = Indent(declaration.RenderedJson(JsonOptions));

            // The section note first, as its own `_comment_<section>` key — the shape the file has
            // always had, so a regenerated file is diff-clean against the curated one.
            if (!string.IsNullOrEmpty(declaration.Section))
            {
                body.Append("  \"_comment_").Append(declaration.Section).Append("\": ")
                    .Append(JsonSerializer.Serialize(declaration.SectionNote, JsonOptions))
                    .Append(",\n");
            }

            body.Append("  \"").Append(declaration.Key).Append("\": ").Append(value);
            if (i < documented.Count - 1) body.Append(',');
            body.Append('\n');
        }

        body.Append("}\n");
        return body.ToString();
    }

    /// <summary>
    /// Render the operator's configuration reference. Every key, including the undocumented ones —
    /// the example file omits those because writing them invites editing them, but a person reading
    /// the reference is entitled to know they exist and why they are not in the file.
    /// </summary>
    public static string RenderMarkdown()
    {
        var page = new StringBuilder();

        page.Append("# ANTHILL — Configuration reference\n\n");
        page.Append("<!-- GENERATED FROM ConfigCatalog. Do not edit by hand: `ConfigCatalogTests`\n");
        page.Append("     regenerates this file and fails on any difference. Change the property's\n");
        page.Append("     `[ConfigKey]` attribute in `AnthillConfig.cs` instead. -->\n\n");
        page.Append("Settings live in `.anthill/config.json`, resolved relative to the working\n");
        page.Append("directory. `anthill --config` prints the active path.\n\n");
        page.Append("**Editable** keys can be changed live through the settings surface under\n");
        page.Append("`manage_settings`. **File-only** keys need a file edit and a restart.\n\n");

        page.Append("| Key | Type | Default | Editable | Env override | Notes |\n");
        page.Append("|---|---|---|---|---|---|\n");

        foreach (var declaration in Declarations.Where(d => d.IsDocumented))
        {
            var value = declaration.Security == ConfigSecurity.Secret
                ? "_(secret)_"
                : "`" + Compact(JsonSerializer.Serialize(declaration.Default, JsonOptions)) + "`";

            var notes = new List<string>();
            if (!string.IsNullOrEmpty(declaration.Summary)) notes.Add(declaration.Summary);
            if (declaration.HasRange) notes.Add($"range {Bound(declaration.Min)}–{Bound(declaration.Max)}");
            if (declaration.Security == ConfigSecurity.Safety) notes.Add("**changes what the colony may do**");
            if (declaration.Aliases.Count > 0) notes.Add("was: " + string.Join(", ", declaration.Aliases));

            page.Append("| `").Append(declaration.Key).Append("` | ")
                .Append(declaration.JsonType).Append(" | ")
                .Append(value).Append(" | ")
                .Append(declaration.IsEditable ? "yes" : "no").Append(" | ")
                .Append(string.IsNullOrEmpty(declaration.EnvOverride) ? "—" : "`" + declaration.EnvOverride + "`")
                .Append(" | ").Append(string.Join("; ", notes)).Append(" |\n");
        }

        var hidden = Declarations.Where(d => !d.IsDocumented).ToList();
        if (hidden.Count > 0)
        {
            page.Append("\n## Deliberately absent from `config.example.json`\n\n");
            page.Append("These are real settings. They are kept out of the example file for the\n");
            page.Append("reason given, so that an operator finds out they exist here rather than\n");
            page.Append("by reading the source.\n\n");
            page.Append("| Key | Why |\n|---|---|\n");
            foreach (var declaration in hidden)
                page.Append("| `").Append(declaration.Key).Append("` | ")
                    .Append(declaration.UndocumentedBecause).Append(" |\n");
        }

        return page.ToString();
    }

    /// <summary>
    /// Re-indent a multi-line JSON value so a nested object sits under its key rather than against
    /// the left margin. The example file is read by people before it is parsed by anything.
    /// </summary>
    private static string Indent(string json)
    {
        if (!json.Contains('\n', StringComparison.Ordinal)) return json;

        var lines = json.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join("\n  ", lines);
    }

    private static string Bound(double value) => double.IsNaN(value) ? "∞" : value.ToString("0.###",
        System.Globalization.CultureInfo.InvariantCulture);

    private static string Compact(string json) =>
        json.Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
