#!/usr/bin/env python
"""Hand-rolled mutation check: apply ONE textual mutation at a time to src/RulesEngine, run the whole xunit
suite (`dotnet test --no-restore`), record whether at least one test failed, then restore the original file.

Usage:
    python scripts/mutation_check.py --check          verify every pattern matches exactly once (no tests run)
    python scripts/mutation_check.py                  run all mutations
    python scripts/mutation_check.py M05 M17          run selected ids

Rules
  * `old` must occur EXACTLY ONCE in its file (after CRLF -> LF); otherwise the mutation is BROKEN, never a survivor.
  * Original bytes are restored in a `finally` block, and src/ is compared with a backup taken before the run.
  * Each mutant gets a per-mutant timeout (MUTANT_TIMEOUT seconds, default 300). A mutant that loops forever
    (for example "regex timeout removed") is only ended by that timeout; a mutant that overflows the stack ends
    when the test host crashes. Both are reported separately, because they are not killed by an assertion.
  * On timeout only the process tree this script started is killed (taskkill /T on Windows, killpg elsewhere).
"""
import filecmp
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SRC = os.path.join(ROOT, "src", "RulesEngine")
DOTNET = os.environ.get("DOTNET", "dotnet")
TIMEOUT = int(os.environ.get("MUTANT_TIMEOUT", "300"))

# (id, description, file, old (must be unique), new)
MUTATIONS = [
    ("M01", "lexer: keywords become case-sensitive", "Lexer.cs",
     "= new(StringComparer.OrdinalIgnoreCase)", "= new(StringComparer.Ordinal)"),
    ("M02", "lexer: \\u escape accepts only decimal digits", "Lexer.cs",
     "NumberStyles.AllowHexSpecifier", "NumberStyles.None"),
    ("M03", "lexer: \\n escape yields the letter n", "Lexer.cs",
     "case 'n': sb.Append('\\n'); i += 2; break;", "case 'n': sb.Append('n'); i += 2; break;"),
    ("M04", "lexer: a carriage return no longer ends a string", "Lexer.cs",
     "if (c == '\\n' || c == '\\r')", "if (c == '\\n')"),
    ("M05", "parser: 'or' binds like 'and'", "Parser.cs",
     "TokenKind.Or => BpOr,", "TokenKind.Or => BpAnd,"),
    ("M06", "parser: * / % bind like + -", "Parser.cs",
     "TokenKind.Star or TokenKind.Slash or TokenKind.Percent => BpMul,", "TokenKind.Star or TokenKind.Slash or TokenKind.Percent => BpAdd,"),
    ("M07", "parser: binary operators become right associative", "Parser.cs",
     "Node right = ParseExpr(bp + 1);", "Node right = ParseExpr(bp);"),
    ("M08", "parser: chained comparisons allowed", "Parser.cs",
     "if (InfixBp(Peek) == BpCmp)", "if (false)"),
    ("M09", "parser: 'not' swallows comparisons differently (binds tighter than ==)", "Parser.cs",
     "Node operand = ParseExpr(BpNot);", "Node operand = ParseExpr(BpCmp + 1);"),
    ("M10", "parser: recursion depth limit effectively removed", "Parser.cs",
     "if (++_nest > maxDepth)", "if (++_nest > maxDepth * 100000)"),
    ("M11", "parser: tree-height limit effectively removed", "Parser.cs",
     "if (node.Depth > maxDepth)", "if (node.Depth > maxDepth * 100000)"),
    ("M12", "parser: 'between' no longer requires 'and'", "Parser.cs",
     "Expect(TokenKind.And, \"'and' in 'between ... and ...'\");", "TryConsume(TokenKind.And);"),
    ("M13", "parser: 'x not between' not recognised", "Parser.cs",
     "is TokenKind.In or TokenKind.Between ? BpCmp : 0,", "is TokenKind.In ? BpCmp : 0,"),
    ("M14", "parser: dotted calls parsed as plain calls (binder still rejects)", "Parser.cs",
     "if (nameToken.Text.Contains('.'))", "if (false)"),
    ("M15", "printer: right operand of same-precedence operator loses its parentheses", "Printer.cs",
     "Write(sb, bin.Right, comparison ? PrecAdd : prec + 1);", "Write(sb, bin.Right, comparison ? PrecAdd : prec);"),
    ("M16", "printer: backslash not escaped in strings", "Printer.cs",
     "case '\\\\': sb.Append(\"\\\\\\\\\"); break;", "case '\\\\': sb.Append(\"\\\\\"); break;"),
    ("M17", "edit distance: transposition costs 2", "EditDistance.cs",
     "v = Math.Min(v, d[i - 2, j - 2] + 1);", "v = Math.Min(v, d[i - 2, j - 2] + 2);"),
    ("M18", "edit distance: one-letter names get suggestions", "EditDistance.cs",
     "int limit = Math.Min(Math.Max(1, input.Length / 3), input.Length - 1);", "int limit = Math.Max(1, input.Length / 3);"),
    ("M19", "edit distance: ties no longer broken by ordinal order", "EditDistance.cs",
     ".OrderBy(c => c, StringComparer.Ordinal)", ".OrderByDescending(c => c, StringComparer.Ordinal)"),
    ("M20", "binder: decimal and double values may mix", "Binder.cs",
     "if (anyDouble && strictDecimal)", "if (false)"),
    ("M21", "binder: decimal literals no longer adapt to double", "Binder.cs",
     "x.Kind == ValueKind.Decimal && x is not BoundConst", "x.Kind == ValueKind.Decimal"),
    ("M22", "binder: paths through nested objects are never null", "Binder.cs",
     "maybeNull = true;", "maybeNull = false;"),
    ("M23", "binder: ordering operators allowed on booleans and enums", "Binder.cs",
     "if (b.Op is not (BinaryOp.Eq or BinaryOp.Ne))", "if (false)"),
    ("M24", "binder: division by a literal zero treated as safe", "Binder.cs",
     "u[1] is BoundConst c && !IsZero(c.Value)", "u[1] is BoundConst c"),
    ("M25", "binder: ambiguous case-insensitive names not detected", "Binder.cs",
     "if (ci.Count > 1)", "if (ci.Count > 100)"),
    ("M26", "binder: regex timeout removed (infinite)", "Binder.cs",
     "new Regex(pattern.Value, RegexOptions.CultureInvariant, options.RegexTimeout)",
     "new Regex(pattern.Value, RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout)"),
    ("M27", "compiler: 'not in' compiled as 'in'", "ExpressionCompiler.cs",
     "BinaryOp op = bi.Negated ? BinaryOp.Ne : BinaryOp.Eq;", "BinaryOp op = bi.Negated ? BinaryOp.Eq : BinaryOp.Eq;"),
    ("M28", "compiler: 'not in' items combined with OR instead of AND", "ExpressionCompiler.cs",
     "bi.Negated ? Expression.AndAlso(acc, term) : Expression.OrElse(acc, term)", "Expression.OrElse(acc, term)"),
    ("M29", "compiler: division by possibly-zero divisor no longer guarded", "ExpressionCompiler.cs",
     "if (a.Op is not (BinaryOp.Div or BinaryOp.Mod) || a.DivisorNonZero)", "if (true)"),
    ("M30", "compiler: nullable boolean treated as true when null", "ExpressionCompiler.cs",
     "Expression.Constant(true, typeof(bool?))", "Expression.Constant(false, typeof(bool?))"),
    ("M31", "compiler: 'is null' on a non-nullable value inverted", "ExpressionCompiler.cs",
     "return Expression.Constant(isn.Negated);", "return Expression.Constant(!isn.Negated);"),
    ("M32", "compiler: startswith compiled as endswith", "ExpressionCompiler.cs",
     "BinaryOp.StartsWith => StartsEnds(nameof(string.StartsWith), l, r),", "BinaryOp.StartsWith => StartsEnds(nameof(string.EndsWith), l, r),"),
    ("M33", "compiler: string operations ignore case", "ExpressionCompiler.cs",
     "Expression.Constant(StringComparison.Ordinal)", "Expression.Constant(StringComparison.OrdinalIgnoreCase)"),
    ("M34", "interpreter: 'or' behaves like 'and'", "Interpreter.cs",
     "return left || (bool)Eval(lg.Right, root)!;", "return left && (bool)Eval(lg.Right, root)!;"),
    ("M35", "interpreter: modulo by zero not guarded", "Interpreter.cs",
     "BinaryOp.Mod => b == T.Zero ? null : a % b,", "BinaryOp.Mod => a % b,"),
    ("M36", "interpreter: daysSince(datetime) rounds instead of truncating", "Interpreter.cs",
     "DateTime dt => (now.UtcDateTime - dt).Ticks / TimeSpan.TicksPerDay,", "DateTime dt => (long)Math.Round((now.UtcDateTime - dt).TotalDays, MidpointRounding.AwayFromZero),"),
    ("M37", "interpreter: explain never short-circuits 'and'", "Interpreter.cs",
     "bool skipRight = lg.IsAnd ? !leftValue : leftValue;", "bool skipRight = false;"),
    ("M38", "rule set: priority order ascending", "RuleSet.cs",
     "list.OrderByDescending(r => r.Priority)", "list.OrderBy(r => r.Priority)"),
    ("M39", "rule set: duplicate names accepted", "RuleSet.cs",
     "if (!seen.Add(d.Name))", "if (false)"),
    ("M40", "runtime: regex timeout escapes as RegexMatchTimeoutException", "RuleCompiler.cs",
     "catch (RegexMatchTimeoutException)", "catch (RegexMatchTimeoutException) when (false)"),
    ("M41", "runtime: daysSince(date) subtracts the wrong way", "RuleCompiler.cs",
     "DateOnly.FromDateTime(now.UtcDateTime).DayNumber - value.DayNumber;", "value.DayNumber - DateOnly.FromDateTime(now.UtcDateTime).DayNumber;"),
    ("M42", "options: default regex timeout 250 ms -> 250 s", "RuleCompiler.cs",
     "TimeSpan.FromMilliseconds(250);", "TimeSpan.FromSeconds(250);"),
]


def read_text(path):
    with open(path, "rb") as f:
        return f.read()


def kill_tree(proc):
    if os.name == "nt":
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)], capture_output=True)
    else:
        try:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        except ProcessLookupError:
            pass


def run_tests():
    """Returns (status, failing tests, seconds). status: survived | killed | killed-timeout | killed-crash | compile-error."""
    kwargs = {}
    if os.name == "nt":
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        kwargs["start_new_session"] = True
    started = time.time()
    proc = subprocess.Popen(
        [DOTNET, "test", os.path.join("tests", "RulesEngine.Tests"), "--no-restore", "-nologo", "-v", "m",
         "-p:TreatWarningsAsErrors=false"],  # mutants such as `if (false)` create unreachable-code warnings; they are not "invalid"
        cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace", **kwargs)
    try:
        out, _ = proc.communicate(timeout=TIMEOUT)
    except subprocess.TimeoutExpired:
        kill_tree(proc)
        try:
            proc.communicate(timeout=30)
        except Exception:
            pass
        return "killed-timeout", [], time.time() - started
    elapsed = time.time() - started
    if re.search(r"error CS\d+", out):
        return "compile-error", [], elapsed
    failing = sorted(set(re.findall(r"^\s*Failed (\S+)", out, re.M)))
    crashed = re.search(r"Test host process crashed|The active test run was aborted|Stack overflow|StackOverflow", out)
    if proc.returncode == 0 and not crashed:
        return "survived", [], elapsed
    if crashed and not failing:
        return "killed-crash", [], elapsed
    if not failing and not re.search(r"Failed!|Failed:", out):
        # non-zero exit without a recognisable failure: treat as invalid rather than a kill
        return "compile-error", [], elapsed
    return "killed", failing, elapsed


def backup_src():
    dest = os.path.join(tempfile.mkdtemp(prefix="rules-engine-src-backup-"), "src")
    shutil.copytree(SRC, dest, ignore=shutil.ignore_patterns("bin", "obj"))
    return dest


def same_tree(a, b):
    cmp = filecmp.dircmp(a, b, ignore=["bin", "obj"])
    if cmp.left_only or cmp.right_only or cmp.funny_files:
        return False
    if not all(filecmp.cmp(os.path.join(a, f), os.path.join(b, f), shallow=False) for f in cmp.common_files):
        return False
    return all(same_tree(os.path.join(a, d), os.path.join(b, d)) for d in cmp.common_dirs)


def main():
    args = sys.argv[1:]
    check_only = "--check" in args
    wanted = {a for a in args if not a.startswith("--")}
    selected = [m for m in MUTATIONS if not wanted or m[0] in wanted]

    originals = {}
    broken = []
    for mid, desc, fname, old, new in selected:
        path = os.path.join(SRC, fname)
        if fname not in originals:
            originals[fname] = read_text(path)
        text = originals[fname].decode("utf-8").replace("\r\n", "\n")
        count = text.count(old)
        if count != 1:
            broken.append((mid, count))
    if broken:
        for mid, count in broken:
            print(f"{mid}: BROKEN pattern matches {count} times (must be exactly 1)")
        return 2
    if check_only:
        print(f"all {len(selected)} patterns match exactly once")
        return 0

    backup = backup_src()
    results = []
    try:
        for mid, desc, fname, old, new in selected:
            path = os.path.join(SRC, fname)
            text = originals[fname].decode("utf-8").replace("\r\n", "\n")
            try:
                with open(path, "w", encoding="utf-8", newline="\n") as f:
                    f.write(text.replace(old, new, 1))
                status, failing, secs = run_tests()
            finally:
                with open(path, "wb") as f:
                    f.write(originals[fname])
            results.append((mid, desc, status, failing, secs))
            print(f"{mid} | {desc} | {status.upper()} | {len(failing)} failing | {secs:.0f}s | {', '.join(failing[:2])}", flush=True)
    finally:
        for fname, data in originals.items():
            with open(os.path.join(SRC, fname), "wb") as f:
                f.write(data)

    identical = same_tree(backup, SRC)
    killed = [r for r in results if r[2].startswith("killed")]
    valid = [r for r in results if r[2] != "compile-error"]
    print()
    print("| id | mutation | result | failing tests | seconds |")
    print("|---|---|---|---:|---:|")
    for mid, desc, status, failing, secs in results:
        label = {"killed": "killed (assertion)", "killed-timeout": "killed (TIMEOUT)", "killed-crash": "killed (test host CRASH)",
                 "survived": "SURVIVED", "compile-error": "invalid (compile error)"}[status]
        print(f"| {mid} | {desc} | {label} | {len(failing)} | {secs:.0f} |")
    print()
    print(f"{len(killed)}/{len(valid)} valid mutants killed "
          f"({sum(1 for r in results if r[2] == 'killed')} by assertion, "
          f"{sum(1 for r in results if r[2] == 'killed-timeout')} by timeout, "
          f"{sum(1 for r in results if r[2] == 'killed-crash')} by test-host crash); "
          f"{sum(1 for r in results if r[2] == 'compile-error')} invalid")
    print(f"src/ identical to the backup taken before the run: {identical}")
    return 0 if identical else 3


if __name__ == "__main__":
    sys.exit(main())
