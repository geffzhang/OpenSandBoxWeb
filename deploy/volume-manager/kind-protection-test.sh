#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cluster="opensandbox-protection-test"
cleanup() { kind delete cluster --name "$cluster" >/dev/null 2>&1 || true; }
trap cleanup EXIT

command -v kind >/dev/null || { echo "需要 kind" >&2; exit 1; }
command -v kubectl >/dev/null || { echo "需要 kubectl" >&2; exit 1; }

kind create cluster --name "$cluster" --wait 120s
kubectl apply -f "$root/namespace-labels.example.yaml"
kubectl apply -f "$root/protection-policy.yaml"
kubectl patch validatingadmissionpolicybinding opensandbox-protect-business-volumes \
  --type=merge -p '{"spec":{"validationActions":["Deny"]}}'

kubectl -n opensandbox apply -f - <<'YAML'
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: protected
  labels:
    opensandbox.io/volume-lifecycle: business
spec:
  accessModes: ["ReadWriteOnce"]
  resources:
    requests:
      storage: 1Gi
YAML

# binding 的 Deny 变更需等待 apiserver 观察；server dry-run 会走准入但不会真正删除 PVC。
for _ in $(seq 1 30); do
  kubectl -n opensandbox delete pvc protected --dry-run=server >/dev/null 2>&1 && sleep 1 || break
done

if kubectl -n opensandbox delete pvc protected --wait=false 2>/dev/null; then
  echo "FAIL: business PVC 删除未被拒绝" >&2
  exit 1
fi
echo "PASS: business PVC 删除被拒绝"

kubectl -n opensandbox label pvc protected opensandbox.io/volume-lifecycle=ephemeral --overwrite
kubectl -n opensandbox delete pvc protected --wait=false
echo "PASS: ephemeral PVC 允许删除"
