"""
Pre-commit coding-notes pattern checker for Claude Code (Windows-compatible).
Triggered via PreToolUse hook on Bash tool calls.
Reads tool input JSON from stdin, skips non-commit commands,
then checks the staged diff against known .claude/CODING_NOTES.md anti-patterns.
"""

import sys
import json
import re
import shlex
import shutil
import subprocess


def _is_git_commit_command(command: str) -> bool:
    """Return True only when command invokes the git commit subcommand."""
    # shlex.split doesn't split on &&/;/| by itself, but it ALSO glues an operator onto an
    # adjacent token when there's no surrounding whitespace (e.g. "git add .&&git commit" ->
    # ['git', 'add', '.&&git', 'commit', ...], the '&&' swallowed into '.&&git') - which hid a
    # later "git" from the token scan below even after an earlier fix for the spaced case.
    # Caught by CodeRabbit (PR #2) with a direct repro. Padding operators with spaces first
    # guarantees they always end up as their own token, regardless of original spacing. Single
    # "&" (background/separator, distinct from "&&") was missing from this list too - also
    # caught by CodeRabbit, same PR, second pass.
    # A raw newline is itself a shell command separator (same as ";"), but shlex treats it as
    # plain whitespace - it just vanishes during tokenization rather than surviving as a token
    # we could check against _SEPARATORS below, so "echo hi\ngit commit" lost the separator
    # entirely and the "git" on the second line was no longer in command position. Caught by
    # CodeRabbit (PR #2), fourth pass. Converting it to ";" first gives it a token that
    # survives, the same (acknowledged, not quote-aware) way the operators below are handled -
    # not a new limitation, just applying the existing one consistently.
    command = command.replace("\n", " ; ")
    command = re.sub(r"(&&|\|\||[;&|])", r" \1 ", command)

    try:
        tokens = shlex.split(command, posix=True)
    except ValueError:
        return bool(re.search(r"git\s+commit", command))

    # Chained commands (git add . && git commit -m x) put a later "git" token after the first
    # one - so every "git" token needs checking, not just the first. Also match git.exe and
    # backslash-style paths (Windows-compatible per this hook's own docstring), which the old
    # `t.endswith("/git")` check missed. Caught by CodeRabbit.
    #
    # Only count a "git"-like token in COMMAND position (index 0, or right after a separator) -
    # otherwise "echo git commit" (just text passed to echo, never actually invoking git) was a
    # false positive, since "git" appeared somewhere in the token stream regardless of whether
    # it was actually being run. Caught by CodeRabbit (PR #2), third pass.
    _SEPARATORS = frozenset({"&&", "||", ";", "&", "|"})
    git_idxs = [
        i for i, t in enumerate(tokens)
        if re.search(r"(^|[\\/])git(\.exe)?$", t, re.IGNORECASE)
        and (i == 0 or tokens[i - 1] in _SEPARATORS)
    ]
    if not git_idxs:
        return False

    # Options that consume the following token as an argument
    _ARG_OPTIONS = frozenset({
        "-C", "-c", "--exec-path", "--git-dir",
        "--work-tree", "--namespace", "--super-prefix",
        "--list-cmds",
    })
    for git_idx in git_idxs:
        i = git_idx + 1
        while i < len(tokens):
            tok = tokens[i]
            if tok in _ARG_OPTIONS:
                i += 2
            elif tok.startswith("-"):
                i += 1
            else:
                if tok == "commit":
                    return True
                break
    return False


def get_staged_diff() -> str:
    git = shutil.which("git")
    if not git:
        return ""
    try:
        result = subprocess.run(
            [git, "diff", "--cached"],
            capture_output=True, text=True
        )
        return result.stdout
    except (OSError, subprocess.SubprocessError):
        return ""


def main():
    try:
        data = json.load(sys.stdin)
        # A PreToolUse hook matched on Bash receives the command nested at tool_input.command, not
        # top-level — reading the top-level key always returned "", so this check silently no-op'd
        # on every single commit since it was written. Confirmed against Claude Code's real
        # PreToolUse hook input schema and caught by CodeRabbit on i-machine-arr/reaparr PR #2.
        command = data.get("tool_input", {}).get("command", "")
    except json.JSONDecodeError:
        sys.exit(0)

    if not _is_git_commit_command(command):
        sys.exit(0)

    diff = get_staged_diff()
    if not diff:
        sys.exit(0)

    added_lines = [line for line in diff.splitlines() if line.startswith("+") and not line.startswith("+++")]

    warnings = []

    # ---------------------------------------------------------------
    # Coding-notes checks — add new entries here as CodeRabbit reviews land
    # ---------------------------------------------------------------

    # Broad catch (Exception ...) — this repo is C#/.NET (BepInEx plugin), not Python,
    # so the template's original "except Exception" check is swapped for its C# equivalent.
    for line in added_lines:
        if re.search(r"catch\s*\(\s*Exception\b", line):
            warnings.append(
                "Broad 'catch (Exception ...)' detected — catch a specific exception type "
                "where possible, or at least log/rethrow rather than swallowing it."
            )
            break

    # ---------------------------------------------------------------

    if warnings:
        print()
        print(f"Coding Notes Pre-commit Check — {len(warnings)} issue(s) found:")
        for w in warnings:
            print(f"  * {w}")
        print()
        print("Review .claude/CODING_NOTES.md before proceeding. Commit is NOT blocked — "
              "fix on next commit if intentional.")
        print()

    sys.exit(0)


if __name__ == "__main__":
    main()
