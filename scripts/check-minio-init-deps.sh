#!/bin/bash
# Fail when the journal object-store provisioner asks its image for a binary that image lacks.
#
# WHY THIS EXISTS. The pinned `minio/mc` release image (upstream `Dockerfile.release`, on
# `ubi9/ubi-micro`) is NOT `FROM scratch` — that is the dev Dockerfile. It ships a shell and
# coreutils-single, so `cat` works there. What it does not ship is `sed`, `awk`, `grep` or
# `envsubst`, and line 76 of the old init.sh called `sed`: it exited 127 — three steps AFTER the
# bucket was created and the runtime user was added. The container died, `restart: on-failure` could not repair it, and the deployment was left with a
# scoped user holding NO policy: every upload, list and download answered `Access Denied`, while
# the bucket and the user both looked correctly created. No CI job could see any of it.
#
# WHAT COUNTS AS A COMMAND. Comments and the shebang are stripped, quoted strings are removed
# (they are data, never commands), and continuation lines are joined, so this reads what the shell
# reads. A name counts only at a command position — start of line, or after `;` `|` `&` `&&` `||`
# `$(` or a backtick. Two consequences that matter:
#
#   - `echo "no mc on PATH"` does not report `on`.
#   - `mc ls` does not report `ls` — `ls` is a MinIO subcommand, not a request for coreutils.
#
# `mc` is the only permitted external command. Everything else must be a shell builtin: read a
# file with $(<file), substitute with ${var//pattern/replacement}, build text with the printf
# builtin.
#
# It is deliberately conservative: it also names shell functions and heredoc words, because a
# guard that runs on one file costs nothing when it over-reports.
set -eu

SCRIPT=${1:-deploy/comms-minio-init/init.sh}

if [ ! -f "$SCRIPT" ]; then
  echo "check-minio-init-deps: no such script: $SCRIPT" >&2
  exit 2
fi

EXTERNAL=$(
grep -vE '^[[:space:]]*#' "$SCRIPT" | grep -v '^#!' \
| sed -E 's/[0-9]*>&?[^ \t)]*/ /g; s/"[^"]*"/STRIPPED/g' \
| sed -E "s/'[^']*'/STRIPPED/g" \
| awk '
    BEGIN {
      split("mc set echo printf read cd export local return exit test true false if then else elif fi for while until do done case esac function shift trap wait eval exec getopts alias umask times ulimit command type hash break continue select time :", ok, " ")
      for (i in ok) allowed[ok[i]] = 1
      logical = ""
    }
    {
      if ($0 ~ /\\[ \t]*$/) { logical = logical " " substr($0, 1, length($0) - 1); next }
      if (logical != "") { $0 = logical " " $0; logical = "" }
      line = $0
      gsub(/[|;][|]?/, "\n", line)
      gsub(/\$\(/, "\n", line)
      gsub(/`/, "\n", line)
      # A quoted word is data. `case "$x" in` and `echo "text on PATH"` must not report
      # `in`/`on`, and `2> /dev/null` must not report a path.
      gsub(/["'\''][^"'\'']*(["'\'']|$)/, "STRIPPED", line)
      gsub(/[ \t](in|to|as|the|a|of|for)[ \t]/, " ", line)
      gsub(/[0-9]*>&?[ \t]*/, "\n", line)
      n = split(line, parts, "\n")
      for (i = 1; i <= n; i++) {
        s = parts[i]
        while (match(s, /^[ \t]+/)) s = substr(s, RSTART + RLENGTH)
        if (s == "") continue
        if (match(s, /^! /)) s = substr(s, 3)
        while (match(s, /^[A-Za-z_][A-Za-z0-9_]*=[^ \t]+[ \t]+/)) s = substr(s, RSTART + RLENGTH)
        if (s == "") continue
        if (match(s, /^[A-Za-z_][A-Za-z0-9_]*=/)) continue
        split(s, w, /[ \t]/)
        cmd = w[1]
        if (cmd == "" || cmd == "STRIPPED") continue
        if (match(cmd, /\//)) cmd = substr(cmd, RSTART + 1)
        if (cmd == "") continue
        if (cmd == "mc") continue
        if (cmd in allowed) continue
        if (cmd ~ /^[A-Za-z0-9_.-]+$/ && cmd !~ /^[0-9]+$/) print cmd
      }
    }
' | sort -u
)

if [ -n "$EXTERNAL" ]; then
  echo "$SCRIPT calls binaries the MinIO client image does not ship:" >&2
  printf '  %s\n' $EXTERNAL >&2
  echo "Only \`mc\` and shell builtins are available there. Read a file with \$(<file)," >&2
  echo "substitute with \${var//pattern/replacement}, build text with the printf builtin." >&2
  exit 1
fi

echo "$SCRIPT calls only mc and shell builtins."
