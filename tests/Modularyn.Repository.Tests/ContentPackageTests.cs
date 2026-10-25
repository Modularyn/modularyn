using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ContentPackageTests
{
    private const string ManifestSchema = "schemas/content-package/package-manifest.schema.json";
    private const string TypeSchema = "schemas/content-package/content-type.schema.json";
    private const string CapabilityMap = "docs/profiles/website-capabilities.json";

    [Fact]
    public void Minimal_package_satisfies_both_schemas_and_semantic_rules()
    {
        Assert.Empty(Validate(ReadPackage()));
    }

    [Fact]
    public void Text_line_endings_do_not_change_media_integrity()
    {
        var files = ReadPackage();
        foreach (var path in files.Keys.Where(path => path.EndsWith(".json", StringComparison.Ordinal)
            || path.EndsWith(".md", StringComparison.Ordinal)).ToArray())
        {
            files[path] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[path]).Replace("\r\n", "\n").Replace("\n", "\r\n"));
        }
        Assert.Empty(Validate(files));
    }

    [Fact]
    public void Documentation_source_resolves_without_network_access()
    {
        var files = ReadPackage();
        AddDocumentation(files);
        Assert.Empty(Validate(files));
    }

    [Fact]
    public void Integer_values_accept_equivalent_JSON_number_notation()
    {
        var files = ReadPackage();
        foreach (var path in new[] { "package.json", "types/page.json", "types/post.json" })
        {
            files[path] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files[path])
                .Replace("\"formatVersion\": 1", "\"formatVersion\": 1.0")
                .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1e0"));
        }
        Assert.Empty(Validate(files));
    }

    [Theory]
    [InlineData("missing-manifest", "required file")]
    [InlineData("missing-type", "required file")]
    [InlineData("missing-entry", "required file")]
    [InlineData("missing-media", "required file")]
    [InlineData("missing-field", "schema")]
    [InlineData("byte-order-mark", "byte-order mark")]
    [InlineData("invalid-utf8", "DecoderFallbackException")]
    [InlineData("bare-cr", "line ending")]
    [InlineData("front-matter", "delimiter")]
    [InlineData("empty-body", "empty Markdown")]
    [InlineData("extra-property", "schema")]
    [InlineData("duplicate-json-property", "duplicate JSON")]
    [InlineData("format-version", "schema")]
    [InlineData("schema-version", "schema")]
    [InlineData("type-format", "schema")]
    [InlineData("invalid-key", "schema")]
    [InlineData("key-newline", "schema")]
    [InlineData("duplicate-type", "duplicate key")]
    [InlineData("duplicate-entry", "duplicate key")]
    [InlineData("duplicate-field", "duplicate key")]
    [InlineData("entry-version", "type version")]
    [InlineData("culture", "culture")]
    [InlineData("invalid-culture", "culture")]
    [InlineData("field-kind", "schema")]
    [InlineData("required-field", "required field")]
    [InlineData("extra-field", "undeclared field")]
    [InlineData("wrong-field-type", "field type")]
    [InlineData("invalid-date", "date-time")]
    [InlineData("content-reference", "reference")]
    [InlineData("content-target", "reference")]
    [InlineData("media-reference", "reference")]
    [InlineData("term-reference", "reference")]
    [InlineData("layout-reference", "renderer")]
    [InlineData("template-reference", "renderer")]
    [InlineData("block-reference", "reference")]
    [InlineData("block-renderer", "renderer")]
    [InlineData("block-version", "renderer")]
    [InlineData("block-shape", "schema")]
    [InlineData("markdown-reference", "reference")]
    [InlineData("markdown-external", "Markdown")]
    [InlineData("markdown-malformed", "Markdown")]
    [InlineData("raw-html", "forbidden")]
    [InlineData("template", "forbidden")]
    [InlineData("encoded-template", "forbidden")]
    [InlineData("unsafe-uri", "forbidden")]
    [InlineData("secret-field", "forbidden")]
    [InlineData("secret-assignment", "forbidden")]
    [InlineData("absolute-path", "path")]
    [InlineData("traversal", "path")]
    [InlineData("reserved-path", "path")]
    [InlineData("unlisted-file", "unlisted")]
    [InlineData("duplicate-path", "duplicate path")]
    [InlineData("media-hash", "media integrity")]
    [InlineData("media-size", "media integrity")]
    [InlineData("media-mime", "media MIME")]
    [InlineData("empty-alt", "schema")]
    [InlineData("term-parent", "parent reference")]
    [InlineData("term-cycle", "parent cycle")]
    [InlineData("menu-parent", "parent reference")]
    [InlineData("menu-cycle", "parent cycle")]
    [InlineData("menu-reference", "reference")]
    [InlineData("menu-depth", "parent depth")]
    [InlineData("route-reference", "reference")]
    [InlineData("route-owner", "canonical route")]
    [InlineData("reserved-route", "reserved route")]
    [InlineData("root-route", "root route")]
    [InlineData("redirect-cycle", "redirect cycle")]
    [InlineData("redirect-target", "redirect target")]
    [InlineData("redirect-collision", "route collision")]
    [InlineData("documentation-reference", "documentation reference")]
    [InlineData("documentation-version", "documentation version")]
    [InlineData("documentation-path", "path")]
    [InlineData("documentation-commit", "schema")]
    public void A_single_package_violation_is_rejected(string mutation, string diagnostic)
    {
        var files = ReadPackage();
        switch (mutation)
        {
            case "missing-manifest": files.Remove("package.json"); break;
            case "missing-type": files.Remove("types/page.json"); break;
            case "missing-entry": files.Remove("entries/about.md"); break;
            case "missing-media": files.Remove("media/garden.png"); break;
            case "missing-field": ChangeManifest(files, m => m.AsObject().Remove("site")); break;
            case "byte-order-mark": files["package.json"] = new byte[] { 239, 187, 191 }.Concat(files["package.json"]).ToArray(); break;
            case "invalid-utf8": files["package.json"] = [255]; break;
            case "bare-cr": files["package.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["package.json"]).Replace("\n", "\r")); break;
            case "front-matter": files["entries/home.md"] = Encoding.UTF8.GetBytes("# A garden\n"); break;
            case "empty-body": ChangeBody(files, "home", _ => "\n"); break;
            case "extra-property": ChangeManifest(files, m => m["extra"] = true); break;
            case "duplicate-json-property":
                files["package.json"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(files["package.json"]).Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"formatVersion\": 1"));
                break;
            case "format-version": ChangeManifest(files, m => m["formatVersion"] = 2); break;
            case "schema-version": ChangeManifest(files, m => m["schemaVersions"]!["entry"] = 2); break;
            case "type-format": ChangeJson(files, "types/page.json", t => t["formatVersion"] = 2); break;
            case "invalid-key": ChangeEntry(files, "home", e => e["key"] = "Home"); break;
            case "key-newline": ChangeEntry(files, "home", e => e["key"] = "home\n"); break;
            case "duplicate-type": ChangeJson(files, "types/post.json", t => t["key"] = "page"); break;
            case "duplicate-entry": ChangeEntry(files, "about", e => e["key"] = "home"); break;
            case "duplicate-field": ChangeJson(files, "types/page.json", t => t["fields"]![1]!["key"] = "summary"); break;
            case "entry-version": ChangeEntry(files, "home", e => e["schemaVersion"] = 2); break;
            case "culture": ChangeEntry(files, "home", e => e["culture"] = "fr-FR"); break;
            case "invalid-culture": ChangeManifest(files, m => m["site"]!["culture"] = "zz-ZZ"); break;
            case "field-kind": ChangeJson(files, "types/page.json", t => t["fields"]![0]!["type"] = "script"); break;
            case "required-field": ChangeEntry(files, "home", e => e["fields"]!.AsObject().Remove("summary")); break;
            case "extra-field": ChangeEntry(files, "home", e => e["fields"]!["extra"] = true); break;
            case "wrong-field-type": ChangeEntry(files, "home", e => e["fields"]!["summary"] = 23); break;
            case "invalid-date": ChangeEntry(files, "first-harvest", e => e["fields"]!["published-at"] = "2026-02-30T09:00:00Z"); break;
            case "content-reference": ChangeEntry(files, "home", e => e["fields"]!["related"] = "absent"); break;
            case "content-target": ChangeEntry(files, "home", e => e["fields"]!["related"] = "first-harvest"); break;
            case "media-reference": ChangeEntry(files, "home", e => e["fields"]!["hero"] = "absent"); break;
            case "term-reference": ChangeEntry(files, "first-harvest", e => e["fields"]!["topic"] = "absent"); break;
            case "layout-reference": ChangeEntry(files, "home", e => e["layout"] = "absent"); break;
            case "template-reference": ChangeEntry(files, "home", e => e["template"] = "absent"); break;
            case "block-reference": ChangeBody(files, "first-harvest", b => b.Replace("\"garden\"", "\"absent\"")); break;
            case "block-renderer": ChangeManifest(files, m => m["renderers"]!.AsArray().RemoveAt(3)); break;
            case "block-version": ChangeManifest(files, m => m["renderers"]![3]!["version"] = 2); break;
            case "block-shape": ChangeBody(files, "first-harvest", b => b.Replace("\"kind\": \"paragraph\"", "\"kind\": \"paragraph\", \"extra\": true")); break;
            case "markdown-reference": ChangeBody(files, "home", b => b.Replace("entry:about", "entry:absent")); break;
            case "markdown-external": ChangeBody(files, "home", b => b.Replace("entry:about", "https://example.invalid")); break;
            case "markdown-malformed": ChangeBody(files, "home", b => b + "\n[unfinished\n"); break;
            case "raw-html": ChangeBody(files, "home", b => b + "\n<strong>text</strong>\n"); break;
            case "template": ChangeBody(files, "home", b => b + "\n{{ name }}\n"); break;
            case "encoded-template": ChangeEntry(files, "home", e => e["title"] = "{{ title }}"); break;
            case "unsafe-uri": ChangeBody(files, "home", b => b + "\njavascript:run()\n"); break;
            case "secret-field": ChangeJson(files, "types/page.json", t => t["fields"]![0]!["key"] = "secret"); break;
            case "secret-assignment": ChangeBody(files, "home", b => b + "\n" + "token=" + new string('q', 32)); break;
            case "absolute-path":
                files["/garden.png"] = files["media/garden.png"];
                break;
            case "traversal": files["media/../garden.png"] = files["media/garden.png"]; break;
            case "reserved-path": files["media/con.png"] = files["media/garden.png"]; break;
            case "unlisted-file": files["extra.json"] = Encoding.UTF8.GetBytes("{}"); break;
            case "duplicate-path": ChangeManifest(files, m => m["entries"]!.AsArray().Add("types/page.json")); break;
            case "media-hash": ChangeManifest(files, m => m["media"]![0]!["sha256"] = new string('0', 64)); break;
            case "media-size": ChangeManifest(files, m => m["media"]![0]!["bytes"] = 1); break;
            case "media-mime": ChangeManifest(files, m => m["media"]![0]!["mimeType"] = "image/jpeg"); break;
            case "empty-alt": ChangeManifest(files, m => m["media"]![0]!["alt"] = " "); break;
            case "term-parent": ChangeManifest(files, m => m["taxonomies"]![0]!["terms"]![0]!["parent"] = "absent"); break;
            case "term-cycle": ChangeManifest(files, m => m["taxonomies"]![0]!["terms"]![0]!["parent"] = "harvest"); break;
            case "menu-parent": ChangeManifest(files, m => m["navigation"]![0]!["nodes"]![0]!["parent"] = "absent"); break;
            case "menu-cycle": ChangeManifest(files, m => m["navigation"]![0]!["nodes"]![0]!["parent"] = "about"); break;
            case "menu-reference": ChangeManifest(files, m => m["navigation"]![0]!["nodes"]![0]!["entry"] = "absent"); break;
            case "menu-depth":
                ChangeManifest(files, m =>
                {
                    var nodes = m["navigation"]![0]!["nodes"]!.AsArray();
                    var parent = "home";
                    for (var i = 0; i < 16; i++)
                    {
                        var key = "child-" + i.ToString(CultureInfo.InvariantCulture);
                        nodes.Add(new JsonObject { ["key"] = key, ["parent"] = parent, ["label"] = "Child", ["entry"] = "home" });
                        parent = key;
                    }
                });
                break;
            case "route-reference": ChangeManifest(files, m => m["routes"]![0]!["entry"] = "absent"); break;
            case "route-owner": ChangeManifest(files, m => m["routes"]![1]!["entry"] = "home"); break;
            case "reserved-route": ChangeManifest(files, m => m["routes"]![1]!["path"] = "/admin"); break;
            case "root-route": ChangeManifest(files, m => m["routes"]![0]!["path"] = "/start"); break;
            case "redirect-cycle": ChangeManifest(files, m => m["redirects"]![0]!["to"] = "/welcome"); break;
            case "redirect-target": ChangeManifest(files, m => m["redirects"]![0]!["to"] = "/missing"); break;
            case "redirect-collision": ChangeManifest(files, m => m["redirects"]![0]!["from"] = "/about"); break;
            case "documentation-reference":
                AddDocumentation(files);
                ChangeManifest(files, m => m["documentationSources"]![0]!["entries"]![0]!["entry"] = "absent");
                break;
            case "documentation-version":
                AddDocumentation(files);
                ChangeManifest(files, m =>
                {
                    var source = m["documentationSources"]![0]!.DeepClone();
                    source["key"] = "guide-copy";
                    m["documentationSources"]!.AsArray().Add(source);
                });
                break;
            case "documentation-path":
                AddDocumentation(files);
                ChangeManifest(files, m => m["documentationSources"]![0]!["root"] = "con");
                break;
            case "documentation-commit":
                AddDocumentation(files);
                ChangeManifest(files, m => m["documentationSources"]![0]!["commit"] = "main");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Contains(Validate(files), error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void Website_map_has_the_exact_scope_and_known_register_ids()
    {
        Assert.Empty(WebsiteCapabilities.Validate(RepositoryFiles.Current[CapabilityMap], RepositoryFiles.Current["docs/coverage/register.json"]));
    }

    [Theory]
    [InlineData("unknown-id", "register id")]
    [InlineData("missing-capability", "exact capability")]
    [InlineData("duplicate-capability", "duplicate key")]
    [InlineData("empty-scope", "scope")]
    [InlineData("empty-reason", "reason")]
    [InlineData("extra-property", "properties")]
    public void Invalid_capability_maps_are_rejected(string mutation, string diagnostic)
    {
        var map = JsonNode.Parse(RepositoryFiles.Current[CapabilityMap])!;
        switch (mutation)
        {
            case "unknown-id": map["included"]![0]!["coverage"]![0]!["id"] = "CMS-999"; break;
            case "missing-capability": map["included"]!.AsArray().RemoveAt(0); break;
            case "duplicate-capability": map["included"]!.AsArray().Add(map["included"]![0]!.DeepClone()); break;
            case "empty-scope": map["included"]![0]!["coverage"]![0]!["scope"] = " "; break;
            case "empty-reason": map["unsupported"]![0]!["reason"] = ""; break;
            case "extra-property": map["extra"] = true; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.Contains(WebsiteCapabilities.Validate(map.ToJsonString(), RepositoryFiles.Current["docs/coverage/register.json"]),
            error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void Schema_checker_refuses_unknown_keywords_and_nonlocal_references()
    {
        var schema = JsonNode.Parse(RepositoryFiles.Current[ManifestSchema])!;
        schema["properties"]!["format"]!["unrecognized"] = true;
        Assert.Contains(Validate(ReadPackage(), schema.ToJsonString()), e => e.Contains("schema keyword", StringComparison.Ordinal));
        schema = JsonNode.Parse(RepositoryFiles.Current[ManifestSchema])!;
        schema["properties"]!["format"] = new JsonObject { ["$ref"] = "urn:unavailable:format" };
        Assert.Contains(Validate(ReadPackage(), schema.ToJsonString()), e => e.Contains("local schema reference", StringComparison.Ordinal));
    }

    private static List<string> Validate(Dictionary<string, byte[]> files, string? manifestSchema = null) =>
        ContentPackageContract.Validate(files, manifestSchema ?? RepositoryFiles.Current[ManifestSchema], RepositoryFiles.Current[TypeSchema]);

    private static Dictionary<string, byte[]> ReadPackage()
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(typeof(ContentPackageTests).Assembly.Location)!);
        while (!File.Exists(Path.Combine(root.FullName, "Modularyn.slnx")))
            root = root.Parent ?? throw new InvalidOperationException("Cannot locate repository.");
        var package = Path.Combine(root.FullName, "docs", "content-packages", "examples", "minimal-site");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        ReadDirectory(package);
        return files;

        void ReadDirectory(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint), "Example package must not contain links.");
                if (attributes.HasFlag(FileAttributes.Directory)) ReadDirectory(path);
                else
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        var executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                        Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(path) & executable);
                    }
                    files.Add(Path.GetRelativePath(package, path).Replace('\\', '/'), File.ReadAllBytes(path));
                }
            }
        }
    }

    private static void ChangeManifest(Dictionary<string, byte[]> files, Action<JsonNode> change) => ChangeJson(files, "package.json", change);

    private static void ChangeJson(Dictionary<string, byte[]> files, string path, Action<JsonNode> change)
    {
        var value = JsonNode.Parse(files[path])!;
        change(value);
        files[path] = Encoding.UTF8.GetBytes(value.ToJsonString());
    }

    private static void ChangeEntry(Dictionary<string, byte[]> files, string key, Action<JsonNode> change)
    {
        var path = "entries/" + key + ".md";
        var (front, body) = ContentPackageContract.SplitEntry(Encoding.UTF8.GetString(files[path]));
        var value = JsonNode.Parse(front)!;
        change(value);
        files[path] = Encoding.UTF8.GetBytes("---json\n" + value.ToJsonString() + "\n---\n" + body);
    }

    private static void ChangeBody(Dictionary<string, byte[]> files, string key, Func<string, string> change)
    {
        var path = "entries/" + key + ".md";
        var (front, body) = ContentPackageContract.SplitEntry(Encoding.UTF8.GetString(files[path]));
        files[path] = Encoding.UTF8.GetBytes("---json\n" + front + "\n---\n" + change(body));
    }

    private static void AddDocumentation(Dictionary<string, byte[]> files) => ChangeManifest(files, m =>
        m["documentationSources"]!.AsArray().Add(new JsonObject
        {
            ["key"] = "guide", ["repository"] = "product-docs", ["version"] = "v1",
            ["commit"] = new string('a', 40), ["root"] = "guide",
            ["entries"] = new JsonArray(new JsonObject { ["entry"] = "about", ["path"] = "about.md" })
        }));
}

// Contract checks for repository fixtures, not a runtime importer or a general schema library.
internal static class ContentPackageContract
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex PathPattern = new("^[a-z0-9][a-z0-9._-]*(?:/[a-z0-9][a-z0-9._-]*)*$", RegexOptions.CultureInvariant);
    private static readonly Regex ReservedDevice = new("^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MarkdownLink = new(@"(?<image>!)?\[(?<label>[^\[\]\r\n]+)\]\((?<kind>entry|media):(?<key>[a-z][a-z0-9]*(?:-[a-z0-9]+)*)\)", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SecretFields = new(["password", "secret", "token", "api-key", "credential", "connection-string"], StringComparer.Ordinal);
    private static readonly HashSet<string> ReservedRoutes = new(["admin", "api", "assets", "preview", "health"], StringComparer.Ordinal);

    public static List<string> Validate(IReadOnlyDictionary<string, byte[]> files, string manifestSchemaText, string typeSchemaText)
    {
        try
        {
            var manifestSchema = Parse(manifestSchemaText);
            var typeSchema = Parse(typeSchemaText);
            SchemaSubset.CheckDocument(manifestSchema, "urn:modularyn:content-package:1:manifest");
            SchemaSubset.CheckDocument(typeSchema, "urn:modularyn:content-package:1:content-type");
            Require(files.Values.Sum(value => (long)value.Length) <= 104857600, "package capacity exceeded");
            var foldedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in files.Keys)
            {
                SafePath(path);
                Require(foldedPaths.Add(path), "duplicate path after case folding");
            }
            var manifest = Parse(Text(Read(files, "package.json")));
            SchemaSubset.Validate(manifest, manifestSchema, manifestSchema);
            var listed = new HashSet<string>(StringComparer.Ordinal) { "package.json" };
            foreach (var path in Items(manifest, "contentTypes").Concat(Items(manifest, "entries")).Select(p => p.GetString()!)
                .Concat(Items(manifest, "media").Select(m => String(m, "path"))))
            {
                SafePath(path);
                Require(listed.Add(path), "duplicate path across manifest lists");
                Read(files, path);
            }
            Require(files.Keys.All(listed.Contains), "unlisted package file");
            InspectText(manifest);
            var culture = String(manifest.GetProperty("site"), "culture");
            Require(CultureInfo.GetCultures(CultureTypes.AllCultures).Any(c => c.Name == culture), "unsupported culture");
            var renderers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var renderer in Items(manifest, "renderers"))
            {
                var kind = String(renderer, "kind");
                Require(renderers.Add(kind + ":" + String(renderer, "key")), "duplicate key for renderer");
                Require(kind != "block" || renderer.GetProperty("version").GetDecimal() == 1, "unsupported block renderer version");
            }
            var types = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var file in Items(manifest, "contentTypes"))
            {
                var path = file.GetString()!;
                Require(path.EndsWith(".json", StringComparison.Ordinal), "type path extension");
                var type = Parse(Text(Read(files, path)));
                SchemaSubset.Validate(type, typeSchema, typeSchema);
                InspectText(type);
                Require(types.TryAdd(String(type, "key"), type), "duplicate key for content type");
                var fields = Index(Items(type, "fields"));
                Require(fields.Keys.All(key => !SecretFields.Contains(key)), "forbidden secret field");
            }
            var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Items(manifest, "entries"))
            {
                var path = file.GetString()!;
                Require(path.EndsWith(".md", StringComparison.Ordinal), "entry path extension");
                var (front, body) = SplitEntry(Text(Read(files, path)));
                var entry = Parse(front);
                SchemaSubset.Validate(entry, manifestSchema.GetProperty("$defs").GetProperty("entry"), manifestSchema);
                InspectText(entry);
                var key = String(entry, "key");
                Require(entries.TryAdd(key, entry), "duplicate key for content entry");
                bodies.Add(key, body);
                Require(String(entry, "culture") == culture, "entry culture must match site culture");
                Require(types.ContainsKey(String(entry, "type")), "missing content type reference");
                var type = types[String(entry, "type")];
                Require(entry.GetProperty("schemaVersion").GetDecimal() == type.GetProperty("schemaVersion").GetDecimal(), "entry type version mismatch");
                Require(renderers.Contains("layout:" + String(entry, "layout")) && renderers.Contains("template:" + String(entry, "template")), "missing renderer dependency");
            }
            var media = Index(Items(manifest, "media"));
            foreach (var item in media.Values)
            {
                var path = String(item, "path");
                var bytes = Read(files, path);
                Require(bytes.LongLength == item.GetProperty("bytes").GetDecimal()
                    && Convert.ToHexStringLower(SHA256.HashData(bytes)) == String(item, "sha256"), "media integrity mismatch");
                ValidateMedia(path, bytes, String(item, "mimeType"));
            }
            var taxonomies = Index(Items(manifest, "taxonomies"));
            foreach (var taxonomy in taxonomies.Values) ValidateParents(Index(Items(taxonomy, "terms")));
            foreach (var type in types.Values)
            {
                foreach (var field in Items(type, "fields"))
                {
                    var kind = String(field, "type");
                    if (kind == "content-ref") Require(types.ContainsKey(String(field, "target")), "field target type reference");
                    if (kind == "term-ref") Require(taxonomies.ContainsKey(String(field, "target")), "field taxonomy reference");
                }
            }
            foreach (var (key, entry) in entries)
            {
                var type = types[String(entry, "type")];
                var definitions = Index(Items(type, "fields"));
                var values = entry.GetProperty("fields");
                Require(values.EnumerateObject().All(p => definitions.ContainsKey(p.Name)), "undeclared field");
                foreach (var (fieldKey, definition) in definitions)
                {
                    if (!values.TryGetProperty(fieldKey, out var value))
                    {
                        Require(!definition.GetProperty("required").GetBoolean(), "missing required field");
                        continue;
                    }
                    ValidateField(value, definition, entries, media, taxonomies);
                }
                if (String(type, "body") == "markdown") ValidateMarkdown(bodies[key], entries, media);
                else
                {
                    var blocks = Parse(bodies[key]);
                    SchemaSubset.Validate(blocks, manifestSchema.GetProperty("$defs").GetProperty("blocks"), manifestSchema);
                    InspectText(blocks);
                    foreach (var block in blocks.EnumerateArray())
                    {
                        var kind = String(block, "kind");
                        Require(renderers.Contains("block:" + kind), "missing block renderer");
                        if (kind == "image") Require(media.ContainsKey(String(block, "media")), "block media reference");
                        if (kind == "content-link") Require(entries.ContainsKey(String(block, "entry")), "block entry reference");
                    }
                }
            }
            foreach (var navigation in Index(Items(manifest, "navigation")).Values)
            {
                var nodes = Index(Items(navigation, "nodes"));
                ValidateParents(nodes);
                Require(nodes.Values.All(node => entries.ContainsKey(String(node, "entry"))), "navigation entry reference");
            }
            ValidateRoutes(manifest, entries);
            ValidateDocumentation(manifest, entries);
            return [];
        }
        catch (ContractViolation error) { return [error.Message]; }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return ["invalid package structure: " + error.GetType().Name];
        }
    }

    private static void ValidateField(JsonElement value, JsonElement definition, Dictionary<string, JsonElement> entries,
        Dictionary<string, JsonElement> media, Dictionary<string, JsonElement> taxonomies)
    {
        var kind = String(definition, "type");
        if (kind == "integer") Require(value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number == decimal.Truncate(number), "integer field type");
        else if (kind == "boolean") Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "boolean field type");
        else
        {
            Require(value.ValueKind == JsonValueKind.String, "string field type");
            var text = value.GetString()!;
            switch (kind)
            {
                case "date-time":
                    Require(DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _), "invalid date-time");
                    break;
                case "content-ref":
                    Require(entries.TryGetValue(text, out var entry) && String(entry, "type") == String(definition, "target"), "content reference target");
                    break;
                case "media-ref": Require(media.ContainsKey(text), "media reference target"); break;
                case "term-ref":
                    Require(Items(taxonomies[String(definition, "target")], "terms").Any(term => String(term, "key") == text), "term reference target");
                    break;
            }
        }
    }

    private static void ValidateMarkdown(string body, Dictionary<string, JsonElement> entries, Dictionary<string, JsonElement> media)
    {
        SafeText(body);
        Require(!string.IsNullOrWhiteSpace(body), "empty Markdown body");
        var remaining = MarkdownLink.Replace(body, match =>
        {
            var isImage = match.Groups["image"].Success;
            var kind = match.Groups["kind"].Value;
            Require(!string.IsNullOrWhiteSpace(match.Groups["label"].Value) && isImage == (kind == "media"), "Markdown link kind or label");
            var target = match.Groups["key"].Value;
            Require((isImage ? media : entries).ContainsKey(target), "Markdown reference target");
            return "";
        });
        Require(!remaining.Contains('[') && !remaining.Contains(']'), "unsupported Markdown link syntax");
    }

    private static void ValidateParents(Dictionary<string, JsonElement> nodes)
    {
        foreach (var key in nodes.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? current = key;
            while (current is not null)
            {
                Require(nodes.ContainsKey(current), "missing parent reference");
                Require(seen.Add(current), "parent cycle");
                Require(seen.Count <= 16, "parent depth exceeds 16");
                current = nodes[current].GetProperty("parent").GetString();
            }
        }
    }

    private static void ValidateRoutes(JsonElement manifest, Dictionary<string, JsonElement> entries)
    {
        var routes = new Dictionary<string, string>(StringComparer.Ordinal);
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in Items(manifest, "routes"))
        {
            var path = String(route, "path");
            Route(path);
            var entry = String(route, "entry");
            Require(entries.ContainsKey(entry), "route entry reference");
            Require(routes.TryAdd(path, entry), "route collision");
            Require(owners.Add(entry), "duplicate canonical route owner");
        }
        Require(owners.SetEquals(entries.Keys), "entry missing canonical route");
        Require(routes.ContainsKey("/"), "missing root route");
        var redirects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var redirect in Items(manifest, "redirects"))
        {
            var from = String(redirect, "from");
            var to = String(redirect, "to");
            Route(from); Route(to);
            Require(!routes.ContainsKey(from) && redirects.TryAdd(from, to), "redirect route collision");
        }
        foreach (var from in redirects.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = from;
            while (!routes.ContainsKey(current))
            {
                Require(seen.Add(current), "redirect cycle");
                Require(seen.Count <= 16, "redirect depth exceeds 16");
                Require(redirects.TryGetValue(current, out var target), "missing redirect target");
                current = target!;
            }
        }
    }

    private static void ValidateDocumentation(JsonElement manifest, Dictionary<string, JsonElement> entries)
    {
        var versions = new HashSet<string>(StringComparer.Ordinal);
        var mappedEntries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Index(Items(manifest, "documentationSources")).Values)
        {
            Require(versions.Add(String(source, "repository") + ":" + String(source, "version")), "duplicate documentation version");
            SafePath(String(source, "root"));
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in Items(source, "entries"))
            {
                var entry = String(mapping, "entry");
                var path = String(mapping, "path");
                SafePath(path);
                Require(paths.Add(path), "duplicate documentation path");
                Require(entries.ContainsKey(entry) && mappedEntries.Add(entry), "documentation reference missing or repeated");
            }
        }
    }

    private static void ValidateMedia(string path, byte[] bytes, string mime)
    {
        var valid = mime switch
        {
            "image/png" => path.EndsWith(".png", StringComparison.Ordinal) && bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => (path.EndsWith(".jpg", StringComparison.Ordinal) || path.EndsWith(".jpeg", StringComparison.Ordinal))
                && bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
            "image/webp" => path.EndsWith(".webp", StringComparison.Ordinal) && bytes.Length >= 12
                && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        Require(valid, "media MIME and extension mismatch");
    }

    private static void Route(string path) => Require(!ReservedRoutes.Contains(path.Split('/').ElementAtOrDefault(1) ?? ""), "reserved route namespace");

    private static void SafePath(string path)
    {
        Require(path.Length <= 240 && !path.Any(char.IsWhiteSpace) && PathPattern.IsMatch(path)
            && path.Split('/').All(part => !part.EndsWith('.') && !ReservedDevice.IsMatch(part)), "unsafe package path");
    }

    private static byte[] Read(IReadOnlyDictionary<string, byte[]> files, string path)
    {
        Require(files.TryGetValue(path, out var bytes), "required file missing: " + path);
        return bytes!;
    }

    private static string Text(byte[] bytes)
    {
        Require(bytes.Length <= 1048576, "text capacity exceeded");
        Require(!bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }), "text byte-order mark forbidden");
        var text = Utf8.GetString(bytes).Replace("\r\n", "\n");
        Require(!text.Contains('\r'), "unsupported text line ending");
        return text;
    }

    public static (string Front, string Body) SplitEntry(string text)
    {
        text = text.Replace("\r\n", "\n");
        Require(text.StartsWith("---json\n", StringComparison.Ordinal), "entry front matter delimiter");
        var end = text.IndexOf("\n---\n", 8, StringComparison.Ordinal);
        Require(end >= 8, "entry closing delimiter");
        return (text[8..end], text[(end + 5)..]);
    }

    internal static JsonElement Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        CheckDuplicates(document.RootElement);
        return document.RootElement.Clone();
    }

    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "duplicate JSON property");
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }

    private static void InspectText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) SafeText(value.GetString()!);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) InspectText(item);
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) { SafeText(property.Name); InspectText(property.Value); }
    }

    private static void SafeText(string text)
    {
        Require(!text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) && !Conventions.HasMachinePath(text), "forbidden control or machine path");
        Require(!text.Contains('<') && !new[] { "{{", "{%", "<%", "@{", "@(", "javascript:", "vbscript:", "data:" }
            .Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase)), "forbidden executable content");
        Require(!Regex.IsMatch(text, @"(?i)\b(password|secret|token|api[-_]?key|credential|connection[-_]?string)\s*[:=]\s*\S+", RegexOptions.CultureInvariant), "forbidden secret assignment");
    }

    internal static Dictionary<string, JsonElement> Index(IEnumerable<JsonElement> values)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var key = String(value, "key");
            Require(key.Length <= 64 && !key.Any(char.IsWhiteSpace) && KeyPattern.IsMatch(key), "malformed stable key");
            Require(result.TryAdd(key, value), "duplicate key: " + key);
        }
        return result;
    }

    internal static IEnumerable<JsonElement> Items(JsonElement value, string name) => value.GetProperty(name).EnumerateArray();
    internal static string String(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    internal static void Require(bool condition, string diagnostic)
    {
        if (!condition) throw new ContractViolation(diagnostic);
    }
}

internal sealed class ContractViolation(string message) : Exception(message)
{
}

// Evaluate only the draft 2020-12 keywords used by the two checked-in schemas.
// Unknown keywords fail closed so a schema edit cannot silently weaken these tests.
internal static class SchemaSubset
{
    private static readonly HashSet<string> Keywords = new(
        ["$schema", "$id", "$defs", "$ref", "title", "description", "type", "const", "enum", "oneOf", "properties",
         "required", "additionalProperties", "items", "minItems", "maxItems", "uniqueItems", "minimum", "maximum",
         "minLength", "maxLength", "pattern"], StringComparer.Ordinal);

    public static void CheckDocument(JsonElement schema, string identity)
    {
        ContentPackageContract.Require(schema.GetProperty("$schema").GetString() == "https://json-schema.org/draft/2020-12/schema"
            && schema.GetProperty("$id").GetString() == identity, "schema draft and identity");
        Check(schema);
    }

    private static void Check(JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        ContentPackageContract.Require(schema.ValueKind == JsonValueKind.Object, "schema must be an object or boolean");
        foreach (var property in schema.EnumerateObject())
        {
            ContentPackageContract.Require(Keywords.Contains(property.Name), "unsupported schema keyword: " + property.Name);
            switch (property.Name)
            {
                case "$ref":
                    ContentPackageContract.Require(property.Value.GetString()!.StartsWith("#/$defs/", StringComparison.Ordinal), "require local schema reference");
                    break;
                case "$defs":
                case "properties":
                    foreach (var child in property.Value.EnumerateObject()) Check(child.Value);
                    break;
                case "items":
                case "additionalProperties": Check(property.Value); break;
                case "oneOf": foreach (var child in property.Value.EnumerateArray()) Check(child); break;
            }
        }
    }

    public static void Validate(JsonElement value, JsonElement schema, JsonElement root)
    {
        if (schema.ValueKind == JsonValueKind.True) return;
        ContentPackageContract.Require(schema.ValueKind != JsonValueKind.False, "schema prohibits value");
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var key = reference.GetString()![8..];
            ContentPackageContract.Require(!key.Contains('/') && root.GetProperty("$defs").TryGetProperty(key, out _), "unresolved local schema reference");
            Validate(value, root.GetProperty("$defs").GetProperty(key), root);
        }
        if (schema.TryGetProperty("const", out var constant))
            ContentPackageContract.Require(JsonElement.DeepEquals(value, constant), "schema constant mismatch");
        if (schema.TryGetProperty("enum", out var choices))
            ContentPackageContract.Require(choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(value, choice)), "schema enum mismatch");
        if (schema.TryGetProperty("oneOf", out var alternatives))
        {
            var matches = 0;
            foreach (var alternative in alternatives.EnumerateArray())
            {
                try { Validate(value, alternative, root); matches++; }
                catch (ContractViolation) { }
            }
            ContentPackageContract.Require(matches == 1, "schema oneOf requires exactly one matching shape");
        }
        if (schema.TryGetProperty("type", out var type))
        {
            var valid = type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && number == decimal.Truncate(number),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => throw new ContractViolation("unsupported schema type")
            };
            ContentPackageContract.Require(valid, "schema type mismatch");
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    ContentPackageContract.Require(value.TryGetProperty(name.GetString()!, out _), "schema missing required property");
            foreach (var property in value.EnumerateObject())
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property.Name, out var child))
                    Validate(property.Value, child, root);
                else if (schema.TryGetProperty("additionalProperties", out var additional)) Validate(property.Value, additional, root);
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            Bound(items.Length, schema, "minItems", "maxItems");
            if (schema.TryGetProperty("items", out var itemSchema))
                foreach (var item in items) Validate(item, itemSchema, root);
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
                for (var i = 0; i < items.Length; i++)
                    for (var j = i + 1; j < items.Length; j++)
                        ContentPackageContract.Require(!JsonElement.DeepEquals(items[i], items[j]), "schema duplicate array item");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            Bound(text.EnumerateRunes().Count(), schema, "minLength", "maxLength");
            if (schema.TryGetProperty("pattern", out var pattern))
                ContentPackageContract.Require(Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "schema string pattern");
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            ContentPackageContract.Require(value.TryGetDecimal(out var number), "schema numeric range");
            Bound(number, schema, "minimum", "maximum");
        }
    }

    private static void Bound(decimal number, JsonElement schema, string minimum, string maximum)
    {
        if (schema.TryGetProperty(minimum, out var low)) ContentPackageContract.Require(number >= low.GetDecimal(), "schema lower bound");
        if (schema.TryGetProperty(maximum, out var high)) ContentPackageContract.Require(number <= high.GetDecimal(), "schema upper bound");
    }
}

internal static class WebsiteCapabilities
{
    private static readonly string[] Included = ["content", "composition", "publication", "media", "taxonomy-navigation", "seo", "blog", "documentation", "contact", "content-packages", "theme", "admin-account", "security", "backup-restore", "upgrade"];
    private static readonly string[] Unsupported = ["multisite-governance", "localization", "search", "engagement", "newsletters", "headless-graphql", "payments-commerce"];

    public static List<string> Validate(string text, string registerText)
    {
        try
        {
            var map = ContentPackageContract.Parse(text);
            var register = ContentPackageContract.Parse(registerText);
            Properties(map, "version", "profile", "gate", "status", "included", "unsupported");
            ContentPackageContract.Require(map.GetProperty("version").GetInt32() == 1
                && ContentPackageContract.String(map, "profile") == "website"
                && ContentPackageContract.String(map, "gate") == "W"
                && ContentPackageContract.String(map, "status") == "defined", "capability map identity");
            var ids = ContentPackageContract.Items(register, "entries").Select(e => ContentPackageContract.String(e, "id")).ToHashSet(StringComparer.Ordinal);
            foreach (var (section, expected) in new[] { ("included", Included), ("unsupported", Unsupported) })
            {
                var capabilities = ContentPackageContract.Index(ContentPackageContract.Items(map, section));
                ContentPackageContract.Require(capabilities.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected), "exact capability scope required");
                foreach (var capability in capabilities.Values)
                {
                    if (section == "unsupported")
                    {
                        Properties(capability, "key", "title", "coverage", "reason");
                        Nonblank(capability, "reason");
                    }
                    else Properties(capability, "key", "title", "coverage");
                    Nonblank(capability, "title");
                    var coverage = ContentPackageContract.Items(capability, "coverage").ToArray();
                    ContentPackageContract.Require(coverage.Length > 0, "coverage must not be empty");
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var reference in coverage)
                    {
                        Properties(reference, "id", "scope");
                        var id = ContentPackageContract.String(reference, "id");
                        ContentPackageContract.Require(ids.Contains(id) && seen.Add(id), "unknown or repeated register id");
                        Nonblank(reference, "scope");
                    }
                }
            }
            return [];
        }
        catch (ContractViolation error) { return [error.Message]; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return ["invalid capability map: " + error.GetType().Name];
        }
    }

    private static void Properties(JsonElement value, params string[] names) =>
        ContentPackageContract.Require(value.ValueKind == JsonValueKind.Object
            && value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names), "capability properties must be exact");

    private static void Nonblank(JsonElement value, string name) =>
        ContentPackageContract.Require(!string.IsNullOrWhiteSpace(ContentPackageContract.String(value, name)), "capability " + name + " must be nonblank");
}
