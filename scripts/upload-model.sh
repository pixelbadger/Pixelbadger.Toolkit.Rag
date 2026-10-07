#!/usr/bin/env sh
# Uploads the local EmbeddingGemma 2 ONNX snapshot (the q8 graphs, onnx/*_quantized.onnx) to the Azure Files share that the Container App mounts at /data,
# as /data/models/embeddinggemma-2-onnx (the service never downloads models). Run it once after the first
# provision, and again with --force after changing the model files.
#
#   scripts/upload-model.sh [--force] [--if-configured] [model-dir]
#
# model-dir defaults to $PBRAG_MODEL_PATH. The resource group comes from $AZURE_RESOURCE_GROUP, or from the current
# azd environment (`azd env get-value AZURE_RESOURCE_GROUP`). Needs the Azure CLI, signed in (`az login`).
# --if-configured (used by the azd postprovision hook) exits quietly when no model directory is configured.
set -eu

force=false
if_configured=false
model_dir=""
for arg in "$@"; do
  case "$arg" in
    --force) force=true ;;
    --if-configured) if_configured=true ;;
    -*) echo "Unknown option: $arg" >&2; exit 2 ;;
    *) model_dir="$arg" ;;
  esac
done
model_dir="${model_dir:-${PBRAG_MODEL_PATH:-}}"

if [ -z "$model_dir" ]; then
  if [ "$if_configured" = true ]; then
    echo "PBRAG_MODEL_PATH is not set: skipping the model upload. Run scripts/upload-model.sh <model-dir> before the app can start."
    exit 0
  fi
  echo "Usage: scripts/upload-model.sh [--force] <model-dir>   (or set PBRAG_MODEL_PATH)" >&2
  exit 2
fi

for f in tokenizer.json config.json processor_config.json \
         onnx/model_quantized.onnx \
         onnx/vision_encoder_quantized.onnx \
         onnx/audio_encoder_quantized.onnx; do
  if [ ! -f "$model_dir/$f" ]; then
    echo "Missing $model_dir/$f (see the README for the files to download)." >&2
    exit 1
  fi
done

rg="${AZURE_RESOURCE_GROUP:-}"
if [ -z "$rg" ] && command -v azd > /dev/null 2>&1; then
  rg="$(azd env get-value AZURE_RESOURCE_GROUP 2> /dev/null || true)"
fi
if [ -z "$rg" ] && [ -n "${AZURE_ENV_NAME:-}" ]; then
  rg="rg-$AZURE_ENV_NAME" # azd's default resource group for an Aspire AppHost
fi
if [ -z "$rg" ]; then
  echo "Set AZURE_RESOURCE_GROUP to the resource group the app was deployed to." >&2
  exit 2
fi

# The Container Apps environment in the group, and its Azure Files storage (the AppHost defines exactly one volume).
env_name="$(az containerapp env list --resource-group "$rg" --query '[0].name' --output tsv)"
if [ -z "$env_name" ]; then
  echo "No Container Apps environment found in resource group '$rg'." >&2
  exit 1
fi
storage_name="$(az containerapp env storage list --resource-group "$rg" --name "$env_name" --query '[0].name' --output tsv)"
account="$(az containerapp env storage show --resource-group "$rg" --name "$env_name" --storage-name "$storage_name" --query 'properties.azureFile.accountName' --output tsv)"
share="$(az containerapp env storage show --resource-group "$rg" --name "$env_name" --storage-name "$storage_name" --query 'properties.azureFile.shareName' --output tsv)"
key="$(az storage account keys list --resource-group "$rg" --account-name "$account" --query '[0].value' --output tsv)"

dest="models/embeddinggemma-2-onnx"
if [ "$force" = false ] && [ "$(az storage file exists --account-name "$account" --account-key "$key" --share-name "$share" \
    --path "$dest/onnx/audio_encoder_quantized.onnx" --query exists --output tsv)" = "true" ]; then
  echo "The model is already in share '$share' ($account). Use --force to upload it again."
  exit 0
fi

echo "Uploading $model_dir to $account/$share/$dest (q8 graphs)..."
az storage directory create --account-name "$account" --account-key "$key" --share-name "$share" --name models --output none
az storage directory create --account-name "$account" --account-key "$key" --share-name "$share" --name "$dest" --output none
az storage directory create --account-name "$account" --account-key "$key" --share-name "$share" --name "$dest/onnx" --output none
# .onnx_data sidecars are optional: uploaded when the q8 graph has one.
for f in tokenizer.json config.json processor_config.json \
         onnx/model_quantized.onnx onnx/model_quantized.onnx_data \
         onnx/vision_encoder_quantized.onnx onnx/vision_encoder_quantized.onnx_data \
         onnx/audio_encoder_quantized.onnx onnx/audio_encoder_quantized.onnx_data; do
  case "$f" in *.onnx_data) [ -f "$model_dir/$f" ] || continue ;; esac
  echo "  $f"
  az storage file upload --account-name "$account" --account-key "$key" --share-name "$share" \
    --source "$model_dir/$f" --path "$dest/$f" --output none
done
# tokenizer_config.json is optional.
if [ -f "$model_dir/tokenizer_config.json" ]; then
  az storage file upload --account-name "$account" --account-key "$key" --share-name "$share" \
    --source "$model_dir/tokenizer_config.json" --path "$dest/tokenizer_config.json" --output none
fi

echo "Done. If the app was already running without the model, restart it:"
echo "  az containerapp revision restart --resource-group $rg --name rag --revision \$(az containerapp show --resource-group $rg --name rag --query properties.latestRevisionName --output tsv)"
