#!/usr/bin/env bash
#
# Install Process Explorer onto this machine, including the privileged helper.
#
# A script rather than a package because the binaries are self-contained Native
# AOT: there is no runtime to resolve and no dependency graph to walk, so the
# whole job is copying a staged filesystem tree into place and deciding whether
# to activate the helper. Distribution packages (see packaging/PKGBUILD) remain
# the better choice where one exists.
#
# The helper is the one decision the script will not make silently. Membership
# of the 'procexp' group is equivalent to sudo — see docs/HELPER.md — so
# activation requires either an interactive yes or an explicit flag.
#
# Usage:
#   sudo ./Scripts/install.sh                 install from artifacts/stage
#   sudo ./Scripts/install.sh --tarball FILE  install from a release tarball
#   sudo ./Scripts/install.sh --uninstall     remove everything, including the
#                                             helper, its unit and its group
#
# Flags:
#   --enable-helper    activate the helper without prompting
#   --without-helper   install the helper's files but do not activate it
#   --add-user NAME    add NAME to the procexp group (implies --enable-helper)
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE="${ROOT}/artifacts/stage"
MANIFEST=/usr/share/procexp/manifest

# Paths removed on uninstall when no manifest exists — an older install, or a
# tarball unpacked by hand. Kept in sync with build-release.sh's staging tree.
FALLBACK_FILES=(
  /usr/bin/procexp
  /usr/bin/procexp-smoke
  /usr/lib/procexp/procexp
  /usr/lib/procexp/libSkiaSharp.so
  /usr/lib/procexp/libHarfBuzzSharp.so
  /usr/lib/procexp/procexp-helper
  /usr/lib/systemd/system/procexp-helper.service
  /usr/share/applications/procexp.desktop
  /usr/share/icons/hicolor/scalable/apps/procexp.svg
  /usr/share/procexp/install.sh
)

# Under `curl | sh` stdin is the pipe, but the controlling terminal can still
# answer questions — so prompts read from /dev/tty when stdin is not one.
PROMPT_IN=""
if [[ -t 0 ]]; then
  PROMPT_IN=/dev/stdin
elif { : < /dev/tty; } 2> /dev/null; then
  PROMPT_IN=/dev/tty
fi

TARBALL=""
UNINSTALL=no
HELPER=ask # ask | yes | no
ADD_USER=""

# The header comment above is the documentation; print it rather than repeating it.
usage() {
  awk 'NR == 1 { next } /^#/ { sub(/^# ?/, ""); print; next } { exit }' "${BASH_SOURCE[0]}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tarball)
      TARBALL="${2:?--tarball needs a file}"
      shift 2
      ;;
    --uninstall)
      UNINSTALL=yes
      shift
      ;;
    --enable-helper)
      HELPER=yes
      shift
      ;;
    --without-helper)
      HELPER=no
      shift
      ;;
    --add-user)
      ADD_USER="${2:?--add-user needs a user name}"
      HELPER=yes
      shift 2
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ ${EUID} -ne 0 ]]; then
  echo "This installs into /usr — run it with sudo." >&2
  exit 1
fi

have() { command -v "$1" > /dev/null 2>&1; }

# The desktop database and icon cache are refreshed so the menu entry appears
# without a logout. Both tools are optional: their absence means a session that
# would not have used them anyway.
refresh_caches() {
  have update-desktop-database && update-desktop-database -q /usr/share/applications || true
  have gtk-update-icon-cache && gtk-update-icon-cache -q /usr/share/icons/hicolor || true
}

uninstall() {
  if have systemctl && [[ -f /usr/lib/systemd/system/procexp-helper.service ]]; then
    systemctl disable --now procexp-helper 2> /dev/null || true
  fi

  local files=()
  if [[ -f "${MANIFEST}" ]]; then
    mapfile -t files < "${MANIFEST}"
  else
    files=("${FALLBACK_FILES[@]}")
  fi

  local f
  for f in "${files[@]}"; do
    rm -f "${f}"
  done
  rm -f "${MANIFEST}"
  rmdir --ignore-fail-on-non-empty /usr/lib/procexp /usr/share/procexp 2> /dev/null || true

  have systemctl && systemctl daemon-reload || true

  # The group is the access grant, so removing the software removes the grant.
  if getent group procexp > /dev/null; then
    groupdel procexp
    echo "Removed the procexp group."
  fi

  refresh_caches
  echo "Uninstalled."
}

if [[ "${UNINSTALL}" == yes ]]; then
  uninstall
  exit 0
fi

# --- Install the files ------------------------------------------------------

# A pre-existing manifest means this is an upgrade, where the helper questions
# were already answered once and should not be asked again.
UPGRADE=no
[[ -f "${MANIFEST}" ]] && UPGRADE=yes

if [[ -n "${TARBALL}" ]]; then
  [[ -f "${TARBALL}" ]] || {
    echo "No such file: ${TARBALL}" >&2
    exit 1
  }
  # The manifest is derived from the tarball listing rather than hardcoded, so
  # uninstall keeps working if a future release adds a file.
  install -d "$(dirname "${MANIFEST}")"
  tar -tzf "${TARBALL}" | grep -v '/$' | sed 's|^\./|/|' > "${MANIFEST}"
  # Extracted to a scratch tree first, then placed by cp, because tar cannot
  # replace files in a live tree: an in-use binary must be unlinked rather
  # than truncated (ETXTBSY), and tar's --unlink-first refuses the non-empty
  # directories it meets on the way. --no-same-owner: everything must end up
  # root-owned, whatever uid built the tarball — the helper runs as root, so
  # a user-owned helper binary would be a privilege escalation.
  scratch="$(mktemp -d)"
  tar -xzf "${TARBALL}" -C "${scratch}" --no-same-owner
  cp -a --remove-destination "${scratch}/." /
  rm -rf "${scratch}"
elif [[ -d "${STAGE}" ]]; then
  install -d "$(dirname "${MANIFEST}")"
  # Symlinks too: /usr/bin/procexp is one.
  (cd "${STAGE}" && find . -type f -o -type l | sed 's|^\./|/|') > "${MANIFEST}"
  # --no-preserve=ownership for the same reason as --no-same-owner above: the
  # stage tree is owned by whoever built it, and the installed files must not
  # be. --remove-destination for the same reason as --unlink-first: a running
  # helper's binary cannot be truncated, only replaced.
  cp -a --no-preserve=ownership --remove-destination "${STAGE}/." /
else
  # Deliberately not built from here: this script runs as root, and a build
  # pulls NuGet packages and runs their toolchain, which should happen as you.
  echo "Nothing to install: ${STAGE} does not exist." >&2
  echo "Build first (as your own user), then re-run this:" >&2
  echo "  ./Scripts/build-release.sh" >&2
  exit 1
fi

refresh_caches
echo "Installed procexp, procexp-smoke and the helper files."

# Replacing the file does not replace the process: a running helper keeps its
# old binary mapped, so restart it to make what runs match what is installed.
if have systemctl && systemctl is-active --quiet procexp-helper 2> /dev/null; then
  systemctl restart procexp-helper
  echo "Restarted the running helper so it picks up the new binary."
fi

# --- Activate the helper ----------------------------------------------------

if ! have systemctl; then
  echo
  echo "systemd not found — the helper only runs under systemd, so it was"
  echo "installed but not activated. The app is fully functional without it."
  exit 0
fi

if [[ "${HELPER}" == ask ]]; then
  # An upgrade is not a new decision. Enabled means the administrator said
  # yes once — keep it (the new binary is already running via the restart
  # above). Installed-but-not-enabled on an upgrade was a no; stay quiet.
  if systemctl is-enabled --quiet procexp-helper 2> /dev/null; then
    HELPER=yes
  elif [[ "${UPGRADE}" == yes ]]; then
    echo
    echo "Helper remains deactivated, as before. To activate:"
    echo "  sudo bash /usr/share/procexp/install.sh --enable-helper"
    exit 0
  fi
fi

if [[ "${HELPER}" == ask ]]; then
  if [[ -n "${PROMPT_IN}" ]]; then
    echo
    echo "The privileged helper fills in the columns and views that /proc"
    echo "restricts to root: other users' I/O, memory detail, environment,"
    echo "open files, and cross-user kill/renice."
    echo
    echo "Membership of its 'procexp' group is equivalent to sudo access:"
    echo "a member can read the environment of any process on the machine,"
    echo "which routinely holds tokens and passwords. See docs/HELPER.md."
    echo
    read -r -p "Enable the helper service? [y/N] " reply < "${PROMPT_IN}"
    [[ "${reply}" =~ ^[Yy] ]] && HELPER=yes || HELPER=no
  else
    # Non-interactive with no flag: the safe default is off, loudly.
    echo
    echo "Helper not activated (non-interactive, no --enable-helper given)."
    echo "To activate later: sudo bash /usr/share/procexp/install.sh --enable-helper"
    exit 0
  fi
fi

if [[ "${HELPER}" == no ]]; then
  echo
  echo "Helper installed but not activated. To activate later:"
  echo "  sudo bash /usr/share/procexp/install.sh --enable-helper"
  exit 0
fi

groupadd -f procexp
systemctl daemon-reload
systemctl enable --now procexp-helper

echo
echo "Helper running:"
systemctl --no-pager --lines 0 status procexp-helper | head -3 || true

# Enabling the service grants nothing by itself — the socket is root:procexp
# 0660 — so group membership is the actual privilege grant, prompted separately.
# Already-granted membership is not asked about again: `id -nG USER` reads the
# group database (including a procexp primary group), not the session.
ALREADY_MEMBER=no
if [[ -n "${SUDO_USER:-}" ]] \
  && id -nG "${SUDO_USER}" 2> /dev/null | tr ' ' '\n' | grep -qx procexp; then
  ALREADY_MEMBER=yes
fi

if [[ -z "${ADD_USER}" && "${ALREADY_MEMBER}" == no && -n "${PROMPT_IN}" && -n "${SUDO_USER:-}" && "${SUDO_USER}" != root ]]; then
  echo
  read -r -p "Add ${SUDO_USER} to the procexp group (sudo-equivalent grant)? [y/N] " reply \
    < "${PROMPT_IN}"
  [[ "${reply}" =~ ^[Yy] ]] && ADD_USER="${SUDO_USER}"
fi

if [[ "${ALREADY_MEMBER}" == yes && -z "${ADD_USER}" ]]; then
  echo "${SUDO_USER} is already in the procexp group."
elif [[ -n "${ADD_USER}" ]]; then
  usermod -aG procexp "${ADD_USER}"
  echo "Added ${ADD_USER} to procexp. Log out and back in for it to take effect."
else
  echo
  echo "No one is in the procexp group yet. To grant access:"
  echo "  sudo usermod -aG procexp USER   # log out and back in afterwards"
fi
