#!/bin/sh
set -eu
# Called explicitly through SSH. Source and destination must be separate directories.
action=${1:-status}
service=pegasus-cerebral
case "$action" in
  status|start|stop|restart) exec systemctl "$action" "$service" ;;
  deploy)
    source_dir=${2:?publish directory required}
    install_dir=${3:?installation directory required}
    run_user=${4:?non-root service user required}
    listen_url=${5:-http://127.0.0.1:2667}
    case "$install_dir" in /|/usr|/opt|/home|/var|/etc|'') echo 'Choose a product-specific installation directory.' >&2; exit 2 ;; esac
    case "$install_dir" in /*) ;; *) echo 'Installation path must be absolute.' >&2; exit 2 ;; esac
    case "$install_dir" in *' '*|*'"'*|*"'"*|*'..'*) echo 'Installation path must not contain spaces, quotes, or parent traversal.' >&2; exit 2 ;; esac
    test "$run_user" != root
    id "$run_user" >/dev/null
    case "$listen_url" in
      http://127.0.0.1:*|http://0.0.0.0:*) ;;
      *) echo 'Listener must be an explicit loopback or IPv4 wildcard HTTP address with a port.' >&2; exit 2 ;;
    esac
    listen_port=${listen_url##*:}
    case "$listen_port" in ''|*[!0-9]*) echo 'Listener port must contain only decimal digits.' >&2; exit 2 ;; esac
    test "$listen_port" -ge 1 -a "$listen_port" -le 65535 || { echo 'Listener port is outside 1-65535.' >&2; exit 2; }
    test -f "$source_dir/Cerebral.Worker"
    test "$(id -u)" = 0 || { echo 'Deployment requires explicit sudo/root access.' >&2; exit 2; }
    command -v curl >/dev/null 2>&1 || { echo 'Deployment requires curl for the loopback health gate.' >&2; exit 2; }
    test "$source_dir" != "$install_dir" || { echo 'Source and installation directories must be different.' >&2; exit 2; }

    install_parent=$(dirname -- "$install_dir")
    install_name=$(basename -- "$install_dir")
    generation=$(date -u +%Y%m%dT%H%M%SZ).$$
    prepared="$install_parent/$install_name.next.$generation"
    rollback="$install_parent/$install_name.rollback.$generation"
    failed="$install_parent/$install_name.failed.$generation"
    unit_path="/etc/systemd/system/$service.service"
    unit_next="$unit_path.next.$generation"
    unit_previous="$unit_path.previous.$generation"
    endpoint_path=/var/lib/pegasus-cerebral/worker-endpoint.txt
    endpoint_previous="$endpoint_path.previous.$generation"
    test ! -e "$prepared" -a ! -e "$rollback" -a ! -e "$failed" || { echo 'Deployment generation paths already exist.' >&2; exit 2; }

    mkdir -p "$install_parent" /var/lib/pegasus-cerebral \
      /var/lib/pegasus-cerebral/models \
      /var/lib/pegasus-cerebral/Projects \
      /var/lib/pegasus-cerebral/Shared \
      /var/lib/pegasus-cerebral/Tools \
      /var/lib/pegasus-cerebral/PluginData \
      /var/lib/pegasus-cerebral/Artifacts/Attachments
    mkdir "$prepared"
    trap 'rm -rf -- "$prepared"; rm -f -- "$unit_next" "$unit_previous" "$endpoint_previous"' EXIT HUP INT TERM
    cp -R "$source_dir/." "$prepared/"
    test -f "$prepared/Cerebral.Worker"
    test -f "$prepared/runtime/llama-server"
    test -f "$prepared/runtime/llama-fit-params"
    chown -R root:root "$prepared"
    chmod +x "$prepared/Cerebral.Worker" \
      "$prepared/runtime/llama-server" \
      "$prepared/runtime/llama-fit-params"
    version_line=$("$prepared/Cerebral.Worker" --version)
    case "$version_line" in
      'Pegasus Cerebral Worker '*) expected_version=${version_line#Pegasus Cerebral Worker } ;;
      *) echo 'Prepared Worker returned an unexpected version identity.' >&2; exit 2 ;;
    esac

    chown -R "$run_user" /var/lib/pegasus-cerebral
    find /var/lib/pegasus-cerebral -type d -exec chmod 700 {} +
    find /var/lib/pegasus-cerebral -type f -exec chmod 600 {} +
    lan_option=
    case "$listen_url" in http://0.0.0.0:*) lan_option=--allow-lan ;; esac
    cat > "$unit_next" <<EOF
[Unit]
Description=Pegasus Cerebral worker
After=network.target
[Service]
Type=notify
User=$run_user
UMask=0077
WorkingDirectory=$install_dir
ExecStart=$install_dir/Cerebral.Worker --data /var/lib/pegasus-cerebral --url $listen_url --auto-port $lan_option
Restart=on-failure
RestartSec=5
TimeoutStopSec=40
[Install]
WantedBy=multi-user.target
EOF
    chmod 644 "$unit_next"

    had_install=no
    had_unit=no
    had_endpoint=no
    was_active=no
    test -e "$install_dir" && had_install=yes
    test -f "$unit_path" && { had_unit=yes; cp -p "$unit_path" "$unit_previous"; }
    test -f "$endpoint_path" && { had_endpoint=yes; cp -p "$endpoint_path" "$endpoint_previous"; }
    systemctl is-active --quiet "$service" && was_active=yes || true

    if (
      set -e
      if systemctl cat "$service" >/dev/null 2>&1; then systemctl stop "$service"; fi
      test "$had_install" = no || mv "$install_dir" "$rollback"
      mv "$prepared" "$install_dir"
      mv "$unit_next" "$unit_path"
      systemctl daemon-reload
      rm -f -- "$endpoint_path" "$endpoint_path.pending"
      systemctl enable --now "$service"
      attempt=0
      healthy=no
      while test "$attempt" -lt 80; do
        selected_url=
        if test -f "$endpoint_path"; then selected_url=$(sed -n '1p' "$endpoint_path"); fi
        selected_port=${selected_url##*:}
        case "$selected_port" in ''|*[!0-9]*) selected_port= ;; esac
        if test -n "$selected_port" && health=$(curl -fsS --max-time 2 "http://127.0.0.1:$selected_port/health" 2>/dev/null); then
          if printf '%s' "$health" | grep -F '"product":"Pegasus Cerebral"' >/dev/null && \
             printf '%s' "$health" | grep -F "\"version\":\"$expected_version\"" >/dev/null && \
             printf '%s' "$health" | grep -F '"architecture":"Arm64"' >/dev/null; then
            healthy=yes
            break
          fi
        fi
        attempt=$((attempt + 1))
        sleep 0.25
      done
      test "$healthy" = yes
      systemctl is-active --quiet "$service"
    ); then
      selected_url=$(sed -n '1p' "$endpoint_path")
      rm -f -- "$unit_previous" "$endpoint_previous"
      trap - EXIT HUP INT TERM
      if test -d "$rollback"; then
        echo "Installed Pegasus Cerebral Worker $expected_version at $selected_url; rollback retained at $rollback"
      else
        echo "Installed Pegasus Cerebral Worker $expected_version at $selected_url"
      fi
      exit 0
    fi

    echo 'Deployment verification failed; restoring the previous generation.' >&2
    trap - EXIT HUP INT TERM
    rollback_error=no
    systemctl stop "$service" >/dev/null 2>&1 || true
    if test -d "$rollback"; then
      test ! -e "$install_dir" || mv "$install_dir" "$failed" || rollback_error=yes
      mv "$rollback" "$install_dir" || rollback_error=yes
    elif test "$had_install" = no -a -e "$install_dir"; then
      mv "$install_dir" "$failed" || rollback_error=yes
    fi
    if test "$had_unit" = yes; then
      test -f "$unit_previous" && mv -f "$unit_previous" "$unit_path" || rollback_error=yes
    else
      rm -f -- "$unit_path" || rollback_error=yes
    fi
    if test "$had_endpoint" = yes; then
      test -f "$endpoint_previous" && mv -f "$endpoint_previous" "$endpoint_path" || rollback_error=yes
    else
      rm -f -- "$endpoint_path" "$endpoint_path.pending" || rollback_error=yes
    fi
    systemctl daemon-reload || rollback_error=yes
    if test "$was_active" = yes; then systemctl start "$service" || rollback_error=yes; fi
    rm -rf -- "$prepared"
    rm -f -- "$unit_next"
    if test "$rollback_error" = yes; then
      echo 'Automatic rollback was incomplete; retain every generation and repair the service explicitly.' >&2
      exit 3
    fi
    rm -f -- "$unit_previous"
    echo "Failed generation retained at $failed" >&2
    exit 1
    ;;
  *) echo 'Usage: install-linux.sh status|start|stop|restart|deploy [source install-directory service-user listen-url]' >&2; exit 2 ;;
esac
