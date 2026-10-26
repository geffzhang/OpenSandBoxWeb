#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail() { echo "FAIL: $*" >&2; exit 1; }

for file in rbac.yaml protection-policy.yaml namespace-labels.example.yaml; do
  [[ -f "$root/$file" ]] || fail "缺少 $file"
done

if grep -Eq 'verbs:.*\b(delete|create|update)\b' "$root/rbac.yaml"; then
  fail "RBAC 不得授予 delete/create/update"
fi
grep -q 'resources: \["namespaces"\]' "$root/rbac.yaml" || fail "缺少 namespaces 规则"
grep -q 'resources: \["persistentvolumeclaims"\]' "$root/rbac.yaml" || fail "缺少 PVC 规则"
grep -q 'resources: \["pods"\]' "$root/rbac.yaml" || fail "缺少 pods 规则"
grep -q 'opensandbox-volume-approvers' "$root/protection-policy.yaml" || fail "缺少审批组"
grep -q 'validationActions: \["Warn", "Audit"\]' "$root/protection-policy.yaml" || fail "初始必须 Warn + Audit"
grep -q "opensandbox.io/volume-managed: \"true\"" "$root/namespace-labels.example.yaml" || fail "缺少受管命名空间标签"

if command -v kubectl >/dev/null 2>&1 && kubectl cluster-info >/dev/null 2>&1; then
  kubectl apply --dry-run=server -f "$root/rbac.yaml" >/dev/null
  kubectl apply --dry-run=server -f "$root/protection-policy.yaml" >/dev/null
  kubectl apply --dry-run=server -f "$root/namespace-labels.example.yaml" >/dev/null
  echo "manifests ok (server dry-run)"
else
  echo "manifests ok (offline; skipped server dry-run)"
fi
