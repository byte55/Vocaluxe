#!/usr/bin/env bash
#
# Vocaluxe launcher. Installed as dist/Vocaluxe.sh by build-linux.sh.
#
# It sits between the starter and the app so that HOW a run ended is on record,
# not just that the window is gone. Vocaluxe writes nothing to stdout/stderr when
# it dies quietly, and neither the kernel nor the journal records the exit code
# of an ordinary user process.
#
# Per run it records
#   - start, pid, runtime, exit code and (if any) signal
#       -> ~/.config/Vocaluxe/Logs/launcher.log and the journal (journalctl -t vocaluxe)
#   - everything the app writes to stdout/stderr (native libraries, .NET runtime)
#       -> ~/.config/Vocaluxe/Logs/stderr/vocaluxe-<timestamp>.log, newest 50 kept
#   - .NET crash dumps with a readable crash report
#       -> ~/.config/Vocaluxe/Dumps/, newest 3 kept
#
# Reading the exit code:
#    0   the app went through its own shutdown path (menu, window close, a startup
#        failure it caught) -- Vocaluxe.log says which, once it logs the reason
#   134  SIGABRT   (.NET fail-fast, unhandled exception on a thread)
#   139  SIGSEGV   (crash in native code)
#   137  SIGKILL   (kill -9, OOM killer)
#   143  SIGTERM   (kill, logout, shutdown)
#
set -u
# Job control: without it bash starts background jobs with SIGINT/SIGQUIT ignored, the app
# would inherit that and never see Ctrl+C or SIGQUIT. The traps below forward them instead.
set -m

DIR="$(cd "$(dirname "$0")/Vocaluxe" && pwd)"
DATA="${VOCALUXE_DATA_DIR:-$HOME/.config/Vocaluxe}"
LOGS="$DATA/Logs"
ERRDIR="$LOGS/stderr"
DUMPDIR="$DATA/Dumps"
LAUNCHER_LOG="$LOGS/launcher.log"

KEEP_STDERR=50
KEEP_DUMPS=3
LAUNCHER_LOG_MAX_LINES=2000

mkdir -p "$ERRDIR" "$DUMPDIR" 2>/dev/null

# prune <dir> <glob> <keep>: delete all but the newest <keep> files
prune() {
    ls -1t "$1"/$2 2>/dev/null | tail -n +"$(($3 + 1))" | while IFS= read -r f; do
        rm -f -- "$f"
    done
}

# note <journal priority> <message>
note() {
    if [ -f "$LAUNCHER_LOG" ] && [ "$(wc -l < "$LAUNCHER_LOG")" -gt "$LAUNCHER_LOG_MAX_LINES" ]; then
        tail -n "$((LAUNCHER_LOG_MAX_LINES / 2))" "$LAUNCHER_LOG" > "$LAUNCHER_LOG.tmp" 2>/dev/null \
            && mv -f "$LAUNCHER_LOG.tmp" "$LAUNCHER_LOG"
    fi
    printf '%s %s\n' "$(date '+%F %T')" "$2" >> "$LAUNCHER_LOG" 2>/dev/null
    logger -t vocaluxe -p "user.$1" -- "$2" 2>/dev/null || true
}

prune "$ERRDIR" 'vocaluxe-*.log' "$KEEP_STDERR"
prune "$DUMPDIR" '*.dmp' "$KEEP_DUMPS"
prune "$DUMPDIR" '*.json' "$KEEP_DUMPS"

# .NET writes a dump (and, with EnableCrashReport, a JSON report with the stack
# traces) when the runtime dies. A heap dump is ~0.9 GB (measured).
export DOTNET_DbgEnableMiniDump=1
export DOTNET_DbgMiniDumpType=2
export DOTNET_DbgMiniDumpName="$DUMPDIR/vocaluxe-%t-%p.dmp"
export DOTNET_EnableCrashReport=1

if [ -x "$DIR/Vocaluxe" ]; then
    CMD=("$DIR/Vocaluxe")                 # self-contained build
else
    CMD=(dotnet "$DIR/Vocaluxe.dll")      # framework-dependent build
fi

START="$(date +%s)"
ERRFILE="$ERRDIR/vocaluxe-$(date +%Y%m%d-%H%M%S).log"

if [ -t 2 ]; then
    # started from a terminal: keep the output visible as well
    "${CMD[@]}" "$@" > >(tee -a "$ERRFILE") 2>&1 &
else
    "${CMD[@]}" "$@" >> "$ERRFILE" 2>&1 &
fi
PID=$!

note info "start pid=$PID args=[$*] output=$ERRFILE"

# Pass termination requests on to the app instead of dying ourselves.
trap 'kill -TERM "$PID" 2>/dev/null' TERM
trap 'kill -INT  "$PID" 2>/dev/null' INT
trap 'kill -HUP  "$PID" 2>/dev/null' HUP

# A trapped signal interrupts "wait" while the app is still running: wait again.
wait "$PID"
RC=$?
while kill -0 "$PID" 2>/dev/null; do
    wait "$PID"
    RC=$?
done

RUNTIME=$(($(date +%s) - START))

SIGNAL=""
if [ "$RC" -gt 128 ]; then
    SIGNAL=" signal=SIG$(kill -l "$((RC - 128))" 2>/dev/null)"
fi

HINT=""
[ "$RC" -eq 0 ] && [ "$RUNTIME" -lt 3 ] && HINT=" (under 3 s: startup failure or second instance)"

DUMPS="$(find "$DUMPDIR" -type f -newermt "@$START" 2>/dev/null | tr '\n' ' ')"
[ -n "$DUMPS" ] && HINT="$HINT dump=[${DUMPS% }]"

if [ "$RC" -eq 0 ]; then PRIO=info; else PRIO=err; fi
note "$PRIO" "end pid=$PID code=$RC$SIGNAL runtime=${RUNTIME}s$HINT"

exit "$RC"
