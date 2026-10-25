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
        var files = ReadPackage();
        Assert.Equal("en-US", JsonNode.Parse(files["package.json"])!["site"]!["culture"]!.GetValue<string>());
        Assert.Empty(Validate(files));
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

    [Theory]
    [InlineData(".")]
    [InlineData("guide")]
    [InlineData("guide/reference")]
    public void Documentation_source_resolves_without_network_access(string root)
    {
        var files = ReadPackage();
        AddDocumentation(files);
        ChangeManifest(files, m => m["documentationSources"]![0]!["root"] = root);
        Assert.Empty(Validate(files));
    }

    [Fact]
    public void Markdown_code_holds_literal_markup_and_prose_may_name_tokens()
    {
        var files = ReadPackage();
        ChangeEntry(files, "about", e => e["title"] = "Metadata: about the garden");
        ChangeBody(files, "home", body => body
            + "\nThe token expires after an hour; the password field is optional.\n"
            + "\nUse `List<string>`, ``a `quoted` span`` and `items[0]` in code.\n"
            + "\n```html\n<p>{{ name }}</p>\n<a href=\"javascript:void(0)\">x</a>\n```\n"
            + "\n~~~csharp\n[Fact] public void Run() { var values = new int[2]; @{ } }\n~~~\n");
        Assert.Empty(Validate(files));
    }

    [Fact]
    public void Block_bodies_allow_two_identical_blocks()
    {
        var files = ReadPackage();
        ChangeBody(files, "first-harvest", body =>
        {
            var block = JsonNode.Parse(body)![0]!;
            return new JsonArray(block.DeepClone(), block.DeepClone()).ToJsonString();
        });
        Assert.Empty(Validate(files));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("eng")]
    [InlineData("en-US")]
    [InlineData("eng-US")]
    public void Culture_grammar_is_checked_without_host_culture_data(string culture)
    {
        var files = ReadPackage();
        ChangeManifest(files, m => m["site"]!["culture"] = culture);
        foreach (var key in new[] { "home", "about", "first-harvest" })
            ChangeEntry(files, key, e => e["culture"] = culture);
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
    [InlineData("missing-manifest", "required file missing: package.json")]
    [InlineData("missing-type", "required file missing: types/page.json")]
    [InlineData("missing-entry", "required file missing: entries/about.md")]
    [InlineData("missing-media", "required file missing: media/garden.png")]
    [InlineData("missing-field", "package.json#/site: schema missing required property")]
    [InlineData("byte-order-mark", "text byte-order mark forbidden")]
    [InlineData("invalid-utf8", "invalid package structure: DecoderFallbackException")]
    [InlineData("bare-cr", "unsupported text line ending")]
    [InlineData("front-matter", "entry front matter delimiter")]
    [InlineData("empty-body", "empty Markdown body")]
    [InlineData("extra-property", "package.json#/extra: schema prohibits value")]
    [InlineData("duplicate-json-property", "duplicate JSON property")]
    [InlineData("format-version", "package.json#/formatVersion: schema constant mismatch")]
    [InlineData("schema-version", "package.json#/schemaVersions/entry: schema constant mismatch")]
    [InlineData("type-format", "types/page.json#/formatVersion: schema constant mismatch")]
    [InlineData("invalid-key", "entries/home.md#/key: schema string pattern")]
    [InlineData("key-newline", "entries/home.md#/key: schema string pattern")]
    [InlineData("duplicate-type", "duplicate key for content type")]
    [InlineData("duplicate-entry", "duplicate key for content entry")]
    [InlineData("duplicate-field", "duplicate key: summary")]
    [InlineData("entry-version", "entry type version mismatch")]
    [InlineData("culture", "entry culture must match site culture")]
    [InlineData("invalid-culture", "package.json#/site/culture: schema string pattern")]
    [InlineData("field-kind", "types/page.json#/fields/0: schema oneOf requires exactly one matching shape")]
    [InlineData("required-field", "missing required field")]
    [InlineData("extra-field", "undeclared field")]
    [InlineData("wrong-field-type", "string field type")]
    [InlineData("invalid-date", "invalid date-time")]
    [InlineData("content-reference", "missing content reference target")]
    [InlineData("content-target", "content reference type mismatch")]
    [InlineData("media-reference", "media reference target")]
    [InlineData("term-reference", "term reference target")]
    [InlineData("layout-reference", "missing layout renderer dependency")]
    [InlineData("template-reference", "missing template renderer dependency")]
    [InlineData("block-reference", "block media reference")]
    [InlineData("block-renderer", "missing block renderer")]
    [InlineData("block-version", "unsupported block renderer version")]
    [InlineData("block-shape", "entries/first-harvest.md#/body/0: schema oneOf requires exactly one matching shape")]
    [InlineData("markdown-reference", "Markdown reference target")]
    [InlineData("markdown-external", "unsupported Markdown link syntax")]
    [InlineData("markdown-malformed", "unsupported Markdown link syntax")]
    [InlineData("raw-html", "forbidden executable content")]
    [InlineData("template", "forbidden executable content")]
    [InlineData("encoded-template", "forbidden executable content")]
    [InlineData("unsafe-uri", "forbidden URI scheme")]
    [InlineData("data-uri", "forbidden URI scheme")]
    [InlineData("title-scheme", "forbidden URI scheme")]
    [InlineData("markup-after-code", "forbidden executable content")]
    [InlineData("unmatched-backtick", "forbidden executable content")]
    [InlineData("unclosed-fence", "unclosed Markdown code fence")]
    [InlineData("fence-info", "Markdown fence info string")]
    [InlineData("link-in-code", "unsupported Markdown link syntax")]
    [InlineData("secret-in-code", "forbidden secret assignment")]
    [InlineData("secret-field", "forbidden secret field")]
    [InlineData("secret-assignment", "forbidden secret assignment")]
    [InlineData("absolute-path", "unsafe package path: /garden.png")]
    [InlineData("traversal", "unsafe package path: media/../garden.png")]
    [InlineData("reserved-path", "unsafe package path: media/con.png")]
    [InlineData("unlisted-file", "unlisted package file")]
    [InlineData("duplicate-path", "duplicate path across manifest lists")]
    [InlineData("media-hash", "media SHA-256 mismatch")]
    [InlineData("media-size", "media byte size mismatch")]
    [InlineData("media-mime", "media MIME and extension mismatch")]
    [InlineData("empty-alt", "package.json#/media/0/alt: schema string pattern")]
    [InlineData("term-parent", "taxonomy topics: missing parent reference")]
    [InlineData("term-cycle", "taxonomy topics: parent cycle")]
    [InlineData("menu-parent", "navigation main: missing parent reference")]
    [InlineData("menu-cycle", "navigation main: parent cycle")]
    [InlineData("menu-reference", "navigation entry reference")]
    [InlineData("menu-depth", "navigation main: parent depth exceeds 16")]
    [InlineData("route-reference", "route entry reference")]
    [InlineData("route-owner", "duplicate canonical route owner")]
    [InlineData("reserved-route", "reserved route namespace")]
    [InlineData("root-route", "missing root route")]
    [InlineData("redirect-cycle", "redirect cycle")]
    [InlineData("redirect-target", "missing redirect target")]
    [InlineData("redirect-collision", "redirect route collision")]
    [InlineData("documentation-reference", "missing documentation entry reference")]
    [InlineData("documentation-version", "duplicate documentation version")]
    [InlineData("documentation-path", "unsafe package path: con")]
    [InlineData("documentation-commit", "package.json#/documentationSources/0/commit: schema string pattern")]
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
            case "invalid-culture": ChangeManifest(files, m => m["site"]!["culture"] = "en-us"); break;
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
            case "data-uri": ChangeBody(files, "home", b => b + "\nSee data:text/html;base64,PHA+\n"); break;
            case "title-scheme": ChangeEntry(files, "home", e => e["title"] = "javascript:run()"); break;
            case "markup-after-code": ChangeBody(files, "home", b => b + "\nUse `code` and then <b>bold</b>.\n"); break;
            case "unmatched-backtick": ChangeBody(files, "home", b => b + "\nA lone ` tick, then <b>bold</b>.\n"); break;
            case "unclosed-fence": ChangeBody(files, "home", b => b + "\n```\n<p>never closed</p>\n"); break;
            case "fence-info": ChangeBody(files, "home", b => b + "\n```html onload\n<p>text</p>\n```\n"); break;
            case "link-in-code": ChangeBody(files, "home", b => b + "\nWrite `[label](https://example.invalid)` here.\n"); break;
            case "secret-in-code": ChangeBody(files, "home", b => b + "\n```\napi_key = " + new string('q', 32) + "\n```\n"); break;
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
        Assert.Equal(diagnostic, Assert.Single(Validate(files)));
    }

    [Theory]
    [InlineData("", "package.json#/documentationSources/0/root: schema minLength lower bound")]
    [InlineData("..", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData("./guide", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData("guide/.", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData("guide/..", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData("/guide", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData("guide/", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData("guide//reference", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData("guide\\reference", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData("Guide", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData(".\n", "package.json#/documentationSources/0/root: schema string pattern")]
    [InlineData("guide.", "unsafe package path: guide.")]
    [InlineData("guide/con.md", "unsafe package path: guide/con.md")]
    public void Invalid_documentation_roots_are_rejected(string root, string diagnostic)
    {
        var files = ReadPackage();
        AddDocumentation(files);
        ChangeManifest(files, m => m["documentationSources"]![0]!["root"] = root);
        Assert.Equal(diagnostic, Assert.Single(Validate(files)));
    }

    [Fact]
    public void Documentation_entry_path_cannot_use_the_root_marker()
    {
        var files = ReadPackage();
        AddDocumentation(files);
        ChangeManifest(files, m =>
        {
            m["documentationSources"]![0]!["root"] = ".";
            m["documentationSources"]![0]!["entries"]![0]!["path"] = ".";
        });
        Assert.Equal("package.json#/documentationSources/0/entries/0/path: schema string pattern", Assert.Single(Validate(files)));
    }

    [Fact]
    public void Website_map_has_the_exact_scope_and_known_register_ids()
    {
        Assert.Empty(WebsiteCapabilities.Validate(RepositoryFiles.Current[CapabilityMap], RepositoryFiles.Current["docs/coverage/register.json"]));
    }

    [Theory]
    [InlineData("unknown-id", "unknown register id: CMS-999")]
    [InlineData("missing-capability", "exact capability scope required")]
    [InlineData("duplicate-capability", "duplicate key: content")]
    [InlineData("empty-scope", "capability scope must be nonblank")]
    [InlineData("empty-reason", "capability reason must be nonblank")]
    [InlineData("extra-property", "capability properties must be exact")]
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
        Assert.Equal(diagnostic, Assert.Single(WebsiteCapabilities.Validate(map.ToJsonString(), RepositoryFiles.Current["docs/coverage/register.json"])));
    }

    [Fact]
    public void Schema_checker_refuses_unknown_keywords_and_nonlocal_references()
    {
        var schema = JsonNode.Parse(RepositoryFiles.Current[ManifestSchema])!;
        schema["properties"]!["format"]!["unrecognized"] = true;
        Assert.Equal("unsupported schema keyword: unrecognized", Assert.Single(Validate(ReadPackage(), schema.ToJsonString())));
        schema = JsonNode.Parse(RepositoryFiles.Current[ManifestSchema])!;
        schema["properties"]!["format"] = new JsonObject { ["$ref"] = "urn:unavailable:format" };
        Assert.Equal("require local schema reference", Assert.Single(Validate(ReadPackage(), schema.ToJsonString())));
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
    private static readonly string[] TemplateDelimiters = ["{{", "{%", "<%", "@{", "@("];
    private static readonly Regex UnsafeScheme = new(@"(?<![A-Za-z0-9+.\-])(?:javascript|vbscript)\s*:|(?<![A-Za-z0-9+.\-])data\s*:\s*[A-Za-z]+/[A-Za-z0-9.+\-]+\s*[;,]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignment = new(@"\b(?:password|passwd|pwd|secret|token|api[-_]?key|access[-_]?key|credential|connection[-_]?string)\s*[:=]\s*[""']?[^\s""']{12,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FenceOpen = new(@"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex FenceInfo = new(@"^[A-Za-z0-9+._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex InlineCode = new(@"(?<!`)(?<ticks>`+)(?!`).*?(?<!`)\k<ticks>(?!`)", RegexOptions.CultureInvariant);
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
            SchemaSubset.Validate(manifest, manifestSchema, manifestSchema, "package.json#");
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
            // Schemas check culture grammar; target culture support is a deployment check.
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
                SchemaSubset.Validate(type, typeSchema, typeSchema, path + "#");
                InspectText(type);
                Require(types.TryAdd(String(type, "key"), type), "duplicate key for content type");
                var fields = Index(Items(type, "fields"));
                Require(fields.Keys.All(key => !SecretFields.Contains(key)), "forbidden secret field");
            }
            var entries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var bodies = new Dictionary<string, (string Path, string Text)>(StringComparer.Ordinal);
            foreach (var file in Items(manifest, "entries"))
            {
                var path = file.GetString()!;
                Require(path.EndsWith(".md", StringComparison.Ordinal), "entry path extension");
                var (front, body) = SplitEntry(Text(Read(files, path)));
                var entry = Parse(front);
                SchemaSubset.Validate(entry, manifestSchema.GetProperty("$defs").GetProperty("entry"), manifestSchema, path + "#");
                InspectText(entry);
                var key = String(entry, "key");
                Require(entries.TryAdd(key, entry), "duplicate key for content entry");
                bodies.Add(key, (path, body));
                Require(String(entry, "culture") == culture, "entry culture must match site culture");
                Require(types.ContainsKey(String(entry, "type")), "missing content type reference");
                var type = types[String(entry, "type")];
                Require(entry.GetProperty("schemaVersion").GetDecimal() == type.GetProperty("schemaVersion").GetDecimal(), "entry type version mismatch");
                Require(renderers.Contains("layout:" + String(entry, "layout")), "missing layout renderer dependency");
                Require(renderers.Contains("template:" + String(entry, "template")), "missing template renderer dependency");
            }
            var media = Index(Items(manifest, "media"));
            foreach (var item in media.Values)
            {
                var path = String(item, "path");
                var bytes = Read(files, path);
                Require(bytes.LongLength == item.GetProperty("bytes").GetDecimal(), "media byte size mismatch");
                Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == String(item, "sha256"), "media SHA-256 mismatch");
                ValidateMedia(path, bytes, String(item, "mimeType"));
            }
            var taxonomies = Index(Items(manifest, "taxonomies"));
            foreach (var taxonomy in taxonomies.Values)
                ValidateParents(Index(Items(taxonomy, "terms")), "taxonomy " + String(taxonomy, "key"));
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
                if (String(type, "body") == "markdown") ValidateMarkdown(bodies[key].Text, entries, media);
                else
                {
                    var blocks = Parse(bodies[key].Text);
                    SchemaSubset.Validate(blocks, manifestSchema.GetProperty("$defs").GetProperty("blocks"), manifestSchema, bodies[key].Path + "#/body");
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
                ValidateParents(nodes, "navigation " + String(navigation, "key"));
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
                    Require(entries.TryGetValue(text, out var entry), "missing content reference target");
                    Require(String(entry, "type") == String(definition, "target"), "content reference type mismatch");
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
        var prose = MarkdownProse(body);
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
        Require(!remaining.Contains("](", StringComparison.Ordinal), "unsupported Markdown link syntax");
        var remainingProse = MarkdownLink.Replace(prose, "");
        Require(!remainingProse.Contains('[') && !remainingProse.Contains(']'), "unsupported Markdown link syntax");
    }

    // Returns the body's text outside code spans and fences, after checking what applies everywhere:
    // characters, machine paths and secrets. Markup, template delimiters and unsafe schemes are
    // rejected only in the returned prose, because the renderer escapes code as literal text.
    private static string MarkdownProse(string body)
    {
        SafeCharacters(body);
        Require(!SecretAssignment.IsMatch(body), "forbidden secret assignment");
        var prose = new StringBuilder();
        Regex? closing = null;
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (closing is not null)
            {
                if (closing.IsMatch(line)) closing = null;
                continue;
            }
            var open = FenceOpen.Match(line);
            if (open.Success)
            {
                var fence = open.Groups["fence"].Value;
                Require(FenceInfo.IsMatch(open.Groups["info"].Value.Trim()), "Markdown fence info string");
                closing = new Regex("^ {0,3}" + Regex.Escape(fence[0].ToString()) + "{" + fence.Length + ",}[ \\t]*$", RegexOptions.CultureInvariant);
                continue;
            }
            prose.Append(InlineCode.Replace(line, " ")).Append('\n');
        }
        Require(closing is null, "unclosed Markdown code fence");
        var text = prose.ToString();
        Require(!text.Contains('<') && !TemplateDelimiters.Any(d => text.Contains(d, StringComparison.Ordinal)), "forbidden executable content");
        Require(!UnsafeScheme.IsMatch(text), "forbidden URI scheme");
        return text;
    }

    private static void ValidateParents(Dictionary<string, JsonElement> nodes, string collection)
    {
        foreach (var key in nodes.Keys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? current = key;
            while (current is not null)
            {
                Require(nodes.ContainsKey(current), collection + ": missing parent reference");
                Require(seen.Add(current), collection + ": parent cycle");
                Require(seen.Count <= 16, collection + ": parent depth exceeds 16");
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
            var root = String(source, "root");
            if (root != ".") SafePath(root);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in Items(source, "entries"))
            {
                var entry = String(mapping, "entry");
                var path = String(mapping, "path");
                SafePath(path);
                Require(paths.Add(path), "duplicate documentation path");
                Require(entries.ContainsKey(entry), "missing documentation entry reference");
                Require(mappedEntries.Add(entry), "duplicate documentation entry reference");
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
            && path.Split('/').All(part => !part.EndsWith('.') && !ReservedDevice.IsMatch(part)), "unsafe package path: " + path);
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
        SafeCharacters(text);
        Require(!text.Contains('<') && !TemplateDelimiters.Any(d => text.Contains(d, StringComparison.Ordinal)), "forbidden executable content");
        Require(!UnsafeScheme.IsMatch(text), "forbidden URI scheme");
        Require(!SecretAssignment.IsMatch(text), "forbidden secret assignment");
    }

    private static void SafeCharacters(string text) =>
        Require(!text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) && !Conventions.HasMachinePath(text), "forbidden control or machine path");

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

    public static void Validate(JsonElement value, JsonElement schema, JsonElement root, string location)
    {
        void Check(bool condition, string diagnostic) => ContentPackageContract.Require(condition, location + ": " + diagnostic);

        if (schema.ValueKind == JsonValueKind.True) return;
        Check(schema.ValueKind != JsonValueKind.False, "schema prohibits value");
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var key = reference.GetString()![8..];
            Check(!key.Contains('/') && root.GetProperty("$defs").TryGetProperty(key, out _), "unresolved local schema reference");
            Validate(value, root.GetProperty("$defs").GetProperty(key), root, location);
        }
        if (schema.TryGetProperty("const", out var constant))
            Check(JsonElement.DeepEquals(value, constant), "schema constant mismatch");
        if (schema.TryGetProperty("enum", out var choices))
            Check(choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(value, choice)), "schema enum mismatch");
        if (schema.TryGetProperty("oneOf", out var alternatives))
        {
            var matches = 0;
            foreach (var alternative in alternatives.EnumerateArray())
            {
                try { Validate(value, alternative, root, location); matches++; }
                catch (ContractViolation) { }
            }
            Check(matches == 1, "schema oneOf requires exactly one matching shape");
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
            Check(valid, "schema type mismatch");
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    ContentPackageContract.Require(value.TryGetProperty(name.GetString()!, out _),
                        PropertyLocation(location, name.GetString()!) + ": schema missing required property");
            foreach (var property in value.EnumerateObject())
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property.Name, out var child))
                    Validate(property.Value, child, root, PropertyLocation(location, property.Name));
                else if (schema.TryGetProperty("additionalProperties", out var additional))
                    Validate(property.Value, additional, root, PropertyLocation(location, property.Name));
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            Bound(items.Length, schema, "minItems", "maxItems", location);
            if (schema.TryGetProperty("items", out var itemSchema))
                for (var i = 0; i < items.Length; i++)
                    Validate(items[i], itemSchema, root, location + "/" + i.ToString(CultureInfo.InvariantCulture));
            if (schema.TryGetProperty("uniqueItems", out var unique) && unique.GetBoolean())
                for (var i = 0; i < items.Length; i++)
                    for (var j = i + 1; j < items.Length; j++)
                        Check(!JsonElement.DeepEquals(items[i], items[j]), "schema duplicate array item");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            Bound(text.EnumerateRunes().Count(), schema, "minLength", "maxLength", location);
            if (schema.TryGetProperty("pattern", out var pattern))
                Check(Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "schema string pattern");
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            Check(value.TryGetDecimal(out var number), "schema numeric range");
            Bound(number, schema, "minimum", "maximum", location);
        }
    }

    private static string PropertyLocation(string location, string name) => location + "/" + name.Replace("~", "~0").Replace("/", "~1");

    private static void Bound(decimal number, JsonElement schema, string minimum, string maximum, string location)
    {
        if (schema.TryGetProperty(minimum, out var low)) ContentPackageContract.Require(number >= low.GetDecimal(), location + ": schema " + minimum + " lower bound");
        if (schema.TryGetProperty(maximum, out var high)) ContentPackageContract.Require(number <= high.GetDecimal(), location + ": schema " + maximum + " upper bound");
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
                        ContentPackageContract.Require(ids.Contains(id), "unknown register id: " + id);
                        ContentPackageContract.Require(seen.Add(id), "repeated register id: " + id);
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
