using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace enki.Tests.Services;

/// <summary>Un ejecutable <c>claude</c> de mentira (script de <c>/bin/sh</c>) que imita lo que Enki usa del
/// CLI real: <c>--version</c>, <c>--help</c>, <c>auth status --json</c> y un turno en <c>-p</c> que emite
/// líneas <c>stream-json</c> preparadas por el test. Guarda los argumentos, la entrada y el entorno que
/// recibió cada turno para que el test pueda comprobar qué se le mandó. Solo funciona en Linux y macOS.</summary>
internal sealed class FakeClaudeCli : IDisposable
{
    public const string HelpText =
        "  --output-format <format>\n  --include-partial-messages\n  --resume [value]\n  --tools <tools...>\n" +
        "  --strict-mcp-config\n  --disable-slash-commands\n  --safe-mode\n  --model <model>\n  --effort <level>\n" +
        "  --system-prompt <prompt>\n  --bare  ... --system-prompt[-file], --append-system-prompt[-file]\n";

    public FakeClaudeCli()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "help.txt"), HelpText);
        Version = "2.1.280 (Claude Code)";
        AuthJson = """{"loggedIn":true,"authMethod":"claude.ai","email":"ana@example.com","subscriptionType":"pro"}""";
        File.WriteAllText(ExecutablePath, """
            #!/bin/sh
            DIR="$(dirname "$0")"
            case "$1" in
              --version) cat "$DIR/version.txt"; exit 0;;
              --help) cat "$DIR/help.txt"; exit 0;;
              auth) cat "$DIR/auth.json"; exit "$(cat "$DIR/auth-exit" 2>/dev/null || echo 0)";;
            esac
            N="$(ls "$DIR" | grep -c '^args-')"
            printf '%s\n' "$@" > "$DIR/args-$N.txt"
            cat > "$DIR/stdin-$N.txt"
            env > "$DIR/env-$N.txt"
            for a in "$@"; do if [ "$PREV" = "--system-prompt-file" ]; then cp "$a" "$DIR/system-$N.txt"; fi; PREV="$a"; done
            if [ -f "$DIR/sleep" ]; then sleep 30; fi
            OUT="$DIR/out.jsonl"
            if grep -q -- '--resume' "$DIR/args-$N.txt" && [ -f "$DIR/resume.jsonl" ]; then OUT="$DIR/resume.jsonl"; fi
            cat "$OUT"
            exit "$(cat "$DIR/exit" 2>/dev/null || echo 0)"
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Reply("sess-1", "Hola", " mundo");
    }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "enki-fake-claude-" + Guid.NewGuid().ToString("N"));
    public string ExecutablePath => Path.Combine(Root, "claude");

    public string Version { set => File.WriteAllText(Path.Combine(Root, "version.txt"), value + "\n"); }
    public string AuthJson { set => File.WriteAllText(Path.Combine(Root, "auth.json"), value + "\n"); }
    public int ExitCode { set => File.WriteAllText(Path.Combine(Root, "exit"), value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    public bool Hangs { set { if (value) File.WriteAllText(Path.Combine(Root, "sleep"), ""); else File.Delete(Path.Combine(Root, "sleep")); } }

    /// <summary>Respuesta correcta en fragmentos, como la emite el CLI con mensajes parciales.</summary>
    public void Reply(string sessionId, params string[] fragments) => WriteOutput("out.jsonl", ReplyLines(sessionId, fragments));

    /// <summary>Respuesta para los turnos que llevan <c>--resume</c>.</summary>
    public void ReplyToResume(IEnumerable<string> lines) => WriteOutput("resume.jsonl", lines);

    public void Output(IEnumerable<string> lines) => WriteOutput("out.jsonl", lines);

    public static IEnumerable<string> ReplyLines(string sessionId, params string[] fragments)
    {
        yield return $$$"""{"type":"system","subtype":"init","session_id":"{{{sessionId}}}","tools":[],"model":"claude-test"}""";
        yield return """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792888200}}""";
        yield return """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}},"parent_tool_use_id":null}""";
        foreach (var fragment in fragments)
            yield return $$$"""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"{{{fragment}}}"}},"parent_tool_use_id":null}""";
        yield return $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{string.Concat(fragments)}}}"}]},"parent_tool_use_id":null}""";
        yield return $$$"""{"type":"result","subtype":"success","is_error":false,"result":"{{{string.Concat(fragments)}}}","session_id":"{{{sessionId}}}"}""";
    }

    public static IEnumerable<string> SessionNotFoundLines(string sessionId) =>
    [
        $$$"""{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"{{{sessionId}}}","errors":["No conversation found with session ID: {{{sessionId}}}"]}"""
    ];

    public int TurnCount => Directory.EnumerateFiles(Root, "args-*.txt").Count();
    public string[] Arguments(int turn) => File.ReadAllLines(Path.Combine(Root, $"args-{turn}.txt"));
    public string Stdin(int turn) => File.ReadAllText(Path.Combine(Root, $"stdin-{turn}.txt"));
    public string Environment(int turn) => File.ReadAllText(Path.Combine(Root, $"env-{turn}.txt"));
    public string SystemPrompt(int turn) => File.ReadAllText(Path.Combine(Root, $"system-{turn}.txt"));

    private void WriteOutput(string file, IEnumerable<string> lines) =>
        File.WriteAllText(Path.Combine(Root, file), string.Join('\n', lines) + "\n");

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
