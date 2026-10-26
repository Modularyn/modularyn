using System.Text;
using System.Text.RegularExpressions;

namespace Modularyn.Repository.Tests;

// A text check of the docker commands in workflows and CI scripts: every image that docker pull, run or create can
// fetch is digest-pinned, or is a local tag that the same workflows build and that run or create never pulls, and every
// build reads a tracked Dockerfile, which the Dockerfile rules check, from a literal context. docs/ci.md states its limits.
internal static partial class ToolPinContract
{
    private const int MaximumResolutionDepth = 5;
    private static readonly Regex DockerCommand = new(
        @"(?<![A-Za-z0-9_.-])docker\s+(?:(?:container|image|buildx)\s+)?(?<verb>run|create|pull|build)(?=\s|$)(?<rest>.*)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex VariableReference = new(@"^\$(?:\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}|(?<name>[A-Za-z_][A-Za-z0-9_]*))$",
        RegexOptions.CultureInvariant);

    private sealed record DockerToken(string Text, bool Dynamic);

    private sealed record DockerUse(string Path, string Verb, List<DockerToken> Options, DockerToken? Positional);

    public static List<string> DockerCommandErrors(IReadOnlyDictionary<string, string> files)
    {
        var errors = new List<string>();
        var sources = files.Where(pair => IsWorkflowPath(pair.Key) || pair.Key.StartsWith(".github/scripts/", StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();
        var workflows = sources.Where(pair => IsWorkflowPath(pair.Key)).Select(pair => pair.Value).ToList();
        var uses = new List<DockerUse>();
        foreach (var (path, text) in sources)
        {
            foreach (var line in LogicalLines(text))
            {
                foreach (Match command in DockerCommand.Matches(line))
                {
                    uses.Add(Parse(path, command.Groups["verb"].Value, command.Groups["rest"].Value));
                }
            }
        }

        // Variables resolve from the workflows' KEY: value lines and from literal shell assignments; in a CI script, a
        // variable can come from any workflow step's environment, so every workflow's definitions count.
        List<string>? Resolve(string path, DockerToken token, int depth = 0)
        {
            if (token.Dynamic || depth > MaximumResolutionDepth)
            {
                return null;
            }

            if (!token.Text.Contains('$', StringComparison.Ordinal))
            {
                return [token.Text];
            }

            var reference = VariableReference.Match(token.Text);
            if (!reference.Success)
            {
                return null;
            }

            var name = reference.Groups["name"].Value;
            var scope = IsWorkflowPath(path) ? [files[path]] : workflows.Append(files[path]).ToList();
            if (scope.Any(text => text.Split('\n').Any(line => line.Contains("GITHUB_ENV", StringComparison.Ordinal)
                    && line.Contains(name + "=", StringComparison.Ordinal))))
            {
                return null;
            }

            var definitions = scope.SelectMany(text => Values(text, name).Concat(Assignments(text, name))).ToList();
            if (definitions.Count == 0)
            {
                return null;
            }

            var values = new List<string>();
            foreach (var definition in definitions)
            {
                var resolved = Resolve(path, Token(definition), depth + 1);
                if (resolved is null)
                {
                    return null;
                }

                values.AddRange(resolved);
            }

            return values;
        }

        var localTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var use in uses.Where(use => use.Verb == "build"))
        {
            foreach (var tag in OptionValues(use, "tag", "t"))
            {
                foreach (var value in Resolve(use.Path, tag) ?? [])
                {
                    localTags.Add(value);
                }
            }
        }

        foreach (var use in uses)
        {
            if (use.Verb == "build")
            {
                var context = use.Positional is null ? null : Resolve(use.Path, use.Positional);
                if (context is null || context.Any(value => value is "" or "-" || value.Contains("://", StringComparison.Ordinal)
                        || value.Contains('$', StringComparison.Ordinal)))
                {
                    errors.Add($"{use.Path}: docker build must name a literal local context, not standard input, a URL or a computed value.");
                }

                var dockerfiles = OptionValues(use, "file").Select(file => Resolve(use.Path, file)).ToList();
                if (dockerfiles.Count != 1 || dockerfiles[0] is not { Count: > 0 } paths
                    || paths.Any(value => !files.ContainsKey(value) || !IsDockerfile(value)))
                {
                    errors.Add($"{use.Path}: docker build must name exactly one tracked Dockerfile with --file=, so the Dockerfile rules cover it.");
                }

                foreach (var option in use.Options.Where(option => option.Text.Contains("docker-image://", StringComparison.Ordinal)))
                {
                    var image = option.Text[(option.Text.IndexOf("docker-image://", StringComparison.Ordinal) + "docker-image://".Length)..];
                    if (option.Dynamic || !IsPinnedImage(image))
                    {
                        errors.Add($"{use.Path}: docker build may take a docker-image:// context only as a digest-pinned reference.");
                    }
                }

                continue;
            }

            var images = use.Positional is null ? null : Resolve(use.Path, use.Positional);
            var neverPulls = OptionValues(use, "pull").Select(value => value.Text).SequenceEqual(["never"]);
            if (images is null)
            {
                errors.Add($"{use.Path}: docker {use.Verb} names an image that this check cannot resolve; give options as --option=value "
                    + "and the image as a literal or a variable with literal definitions.");
                continue;
            }

            foreach (var image in images.Where(image => !IsPinnedImage(image)
                         && !(use.Verb != "pull" && neverPulls && localTags.Contains(image))))
            {
                errors.Add($"{use.Path}: docker {use.Verb} uses {image}, which is not digest-pinned"
                    + (use.Verb == "pull" ? "." : " or a local tag that a workflow builds, used with --pull=never."));
            }
        }

        return errors;
    }

    private static bool IsWorkflowPath(string path) => path.StartsWith(".github/workflows/", StringComparison.Ordinal)
        && (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    // Options are --name=value or flags; the first other word is the image, or for a build its context. An option written
    // as "--name value" therefore makes its value the positional word, which fails closed.
    private static DockerUse Parse(string path, string verb, string rest)
    {
        var options = new List<DockerToken>();
        DockerToken? positional = null;
        foreach (var token in Tokens(rest))
        {
            if (token.Text.StartsWith('-'))
            {
                options.Add(token);
                continue;
            }

            positional = token;
            break;
        }

        return new DockerUse(path, verb, options, positional);
    }

    // The values of --name=value options, or -n=value for a one-letter name.
    private static List<DockerToken> OptionValues(DockerUse use, params string[] names) =>
        use.Options.Select(option => (option, at: option.Text.IndexOf('=', StringComparison.Ordinal)))
            .Where(pair => pair.at > 0 && names.Any(name => pair.option.Text[..pair.at] == (name.Length == 1 ? "-" : "--") + name))
            .Select(pair => new DockerToken(pair.option.Text[(pair.at + 1)..], pair.option.Dynamic)).ToList();

    // Shell words up to the end of the command: whitespace separates words outside quotes, quotes are removed, and an
    // unquoted operator or closing parenthesis ends the command. A word with a command substitution is dynamic.
    private static List<DockerToken> Tokens(string text)
    {
        var tokens = new List<DockerToken>();
        var word = new StringBuilder();
        var dynamic = false;
        var started = false;
        char? quote = null;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                    continue;
                }

                dynamic |= quote == '"' && (character == '`' || character == '$' && index + 1 < text.Length && text[index + 1] == '(');
                word.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(character) || character is '|' or '&' or ';' or '<' or '>' or ')' or '`')
            {
                if (started)
                {
                    tokens.Add(new DockerToken(word.ToString(), dynamic));
                    word.Clear();
                    dynamic = false;
                    started = false;
                }

                if (char.IsWhiteSpace(character))
                {
                    continue;
                }

                break;
            }

            dynamic |= character == '$' && index + 1 < text.Length && text[index + 1] == '(';
            word.Append(character);
            started = true;
        }

        if (started)
        {
            tokens.Add(new DockerToken(word.ToString(), dynamic || quote is not null));
        }

        return tokens;
    }

    private static DockerToken Token(string definition)
    {
        var tokens = Tokens(definition);
        return tokens.Count == 1 ? tokens[0] : new DockerToken(definition, Dynamic: true);
    }

    // Literal shell assignments of a variable, NAME=value, with an optional local, export or readonly.
    private static IEnumerable<string> Assignments(string text, string name) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select(line => Regex.Match(line, $@"^\s*(?:(?:local|export|readonly)\s+)?{Regex.Escape(name)}=(?<value>.*?)\s*$", RegexOptions.CultureInvariant))
            .Where(match => match.Success).Select(match => match.Groups["value"].Value);
}
