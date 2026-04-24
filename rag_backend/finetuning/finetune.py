import os
os.environ["HF_HOME"] = "/media/volume/AI_Core_CoDIRA_StorageVolume/huggingface_cache"
os.environ["CUDA_VISIBLE_DEVICES"] = "0"

# The root disk (/) on this VM is only ~58 GB and gets filled by HF caches,
# dataset map caches, and TRL scratch. Redirect every tempfile flavor to the
# big data volume so /tmp never runs the system out of space mid-training.
_BIG_TMP = "/media/volume/AI_Core_CoDIRA_StorageVolume/tmp"
os.makedirs(_BIG_TMP, exist_ok=True)
for _v in ("TMPDIR", "TEMP", "TMP"):
    os.environ[_v] = _BIG_TMP
# Datasets/HF have their own cache locations — pin them to the volume too.
os.environ.setdefault(
    "HF_DATASETS_CACHE",
    "/media/volume/AI_Core_CoDIRA_StorageVolume/huggingface_cache/datasets",
)
# NOTE: do NOT set expandable_segments on this host.
# The GPU here is a GRID A100X-20C vGPU, and vGPU profiles don't expose the
# CUDA virtual-memory APIs (cuMemCreate/cuMemMap) that expandable_segments
# relies on, which causes `RuntimeError: CUDA driver error: operation not supported`
# on the very first torch.empty on CUDA. Leave the allocator at default.
os.environ.pop("PYTORCH_CUDA_ALLOC_CONF", None)

import gc
import json
import torch

# ── Compatibility shim: bitsandbytes 0.49.2 ↔ accelerate ≥1.13 ────────────────
# When accelerate's dispatch_model path wraps tensors (e.g. while setting up
# CPU-offload hooks with device_map="auto"), it forwards kwargs collected from
# the original param's __dict__, including HF-private ones like
# `_is_hf_initialized`. bitsandbytes' Params4bit.__new__ / Int8Params.__new__
# have strict signatures without **kwargs, so the call blows up with
#   TypeError: Params4bit.__new__() got an unexpected keyword argument '_is_hf_initialized'
# Patch both subclasses to silently drop any private underscore-prefixed
# kwargs before calling the real __new__. Must run BEFORE transformers/peft
# pull bitsandbytes in.
import bitsandbytes as _bnb

def _make_kwarg_tolerant(cls):
    _orig_new = cls.__new__
    def _tolerant_new(_cls, *args, **kwargs):
        for k in [k for k in kwargs if k.startswith("_")]:
            kwargs.pop(k, None)
        return _orig_new(_cls, *args, **kwargs)
    cls.__new__ = _tolerant_new

_make_kwarg_tolerant(_bnb.nn.Params4bit)
_make_kwarg_tolerant(_bnb.nn.Int8Params)
# ──────────────────────────────────────────────────────────────────────────────

from datasets import Dataset
from transformers import AutoTokenizer, AutoModelForCausalLM, BitsAndBytesConfig
from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training, PeftModel
from trl import SFTTrainer, SFTConfig

# ── Config ─────────────────────────────────────────────────────────────────────
# medgemma-27b-text-it on a 20 GB vGPU (GRID A100X-20C):
#   4-bit weights alone ≈ 14 GB, and prepare_model_for_kbit_training wants
#   another ~5 GB to upcast embeds/norms to fp32 → OOM without CPU offload.
#   We use device_map="auto" + a GPU cap + llm_int8_enable_fp32_cpu_offload=True
#   so the quantized transformer blocks stay on GPU while embeddings and the
#   lm_head spill to CPU. Training works but is slow due to PCIe traffic.
MODEL_ID   = "google/medgemma-27b-text-it"
DATA_PATH  = "/home/exouser/Desktop/Emergency-Room-Simulator-Repository/rag_backend/finetuning/data/medgemma_finetuning_data.json"
OUTPUT_DIR = "/media/volume/AI_Core_CoDIRA_StorageVolume/medgemma-finetuned"

# Leave ~5 GB of GPU headroom for activations, LoRA optimizer state and
# the fp32 upcast of any norm params that stay on GPU.
GPU_MEM_CAP = "14GiB"
CPU_MEM_CAP = "100GiB"

# ── Step 1: Flip roles so the model learns to be the GP ───────────────────────
#
# Original data:  user=GP asking questions, assistant=patient answering
# We want:        model learns to BE the GP
#
# Strategy: break each long conversation into overlapping windows.
# Each window shows the conversation history so far, and the model
# must predict the NEXT GP question (1-2 questions at a time).
#
def build_gp_training_examples(raw_data):
    """
    Converts patient-GP conversations into training examples
    where the model plays the GP role.

    Each example = conversation history up to a GP turn,
    target = that GP's response (1-2 focused questions).
    """
    examples = []

    GP_SYSTEM = (
        "You are a warm, empathetic GP (general practitioner). "
        "Use plain, simple language. Ask only 1-2 focused questions at a time. "
        "Show empathy and good bedside manner."
        "Never overwhelm the patient with too many questions at once."
    )

    for convo in raw_data:
        original_messages = convo["messages"]

        # Separate out messages, skipping the original system prompt
        # user = GP in original data, assistant = patient in original data
        turns = [m for m in original_messages if m["role"] in ("user", "assistant")]

        # Build sliding window: for each GP turn, create one training example
        # showing everything before it as context
        history = []  # conversation history from model's perspective (as GP)

        for i, turn in enumerate(turns):
            if turn["role"] == "user":
                # This is a GP turn in original data → model should learn this
                gp_response = turn["content"]

                # Build the training example:
                # history so far (as patient responses) + model generates GP question
                messages = [{"role": "system", "content": GP_SYSTEM}]

                # Add conversation history so far (model=GP, user=patient)
                for h in history:
                    messages.append(h)

                # The target: model produces this GP response
                messages.append({"role": "assistant", "content": gp_response})

                examples.append({"messages": messages})

                # Update history: add this GP turn as "assistant"
                history.append({"role": "assistant", "content": gp_response})

            elif turn["role"] == "assistant":
                # This is the patient's response → becomes "user" turn in our flipped setup
                history.append({"role": "user", "content": turn["content"]})

    print(f"Created {len(examples)} training examples from {len(raw_data)} conversations")
    return examples


# ── Step 2: Load & process data ────────────────────────────────────────────────
print("Loading and processing data...")
with open(DATA_PATH) as f:
    raw_data = json.load(f)

examples = build_gp_training_examples(raw_data)
dataset = Dataset.from_list(examples)
print(f"Dataset: {dataset}")

# Quick sanity check — print one example
print("\n=== Sample training example ===")
sample = examples[5]  # skip first few to see a mid-conversation example
for m in sample["messages"][-3:]:  # last 3 messages
    print(f"[{m['role'].upper()}]: {m['content'][:200]}")
print("================================\n")

# ── Step 3: Load Tokenizer ─────────────────────────────────────────────────────
print("Loading tokenizer...")
tokenizer = AutoTokenizer.from_pretrained(MODEL_ID)
tokenizer.pad_token = tokenizer.eos_token
tokenizer.padding_side = "right"

# ── Step 4: Load Model in 4-bit (with CPU offload for 27B on 20 GB) ───────────
print("Loading model in 4-bit with CPU offload...")
quantization_config = BitsAndBytesConfig(
    load_in_4bit=True,
    bnb_4bit_compute_dtype=torch.bfloat16,
    bnb_4bit_quant_type="nf4",
    bnb_4bit_use_double_quant=True,
    # Required when device_map spills non-quantized params (embeds, lm_head,
    # layer norms) to CPU while keeping 4-bit blocks on GPU. The flag is
    # misleadingly named — it's also honored for 4-bit loads.
    llm_int8_enable_fp32_cpu_offload=True,
)

model = AutoModelForCausalLM.from_pretrained(
    MODEL_ID,
    quantization_config=quantization_config,
    device_map="auto",
    max_memory={0: GPU_MEM_CAP, "cpu": CPU_MEM_CAP},
)
# use_cache must be off when gradient checkpointing is on.
model.config.use_cache = False
model = prepare_model_for_kbit_training(
    model, use_gradient_checkpointing=True
)
model.gradient_checkpointing_enable(gradient_checkpointing_kwargs={"use_reentrant": False})

# ── Step 5: LoRA Adapter ───────────────────────────────────────────────────────
lora_config = LoraConfig(
    r=8,
    lora_alpha=16,
    target_modules=["q_proj", "k_proj", "v_proj", "o_proj"],
    lora_dropout=0.05,
    bias="none",
    task_type="CAUSAL_LM"
)

model = get_peft_model(model, lora_config)
model.print_trainable_parameters()

# ── Step 6: Format conversations into Gemma prompt format ─────────────────────
def formatting_prompts_func(example):
    messages = example["messages"]
    text = ""
    system_prompt = ""

    for msg in messages:
        if msg["role"] == "system":
            system_prompt = msg["content"]
        elif msg["role"] == "user":
            content = msg["content"]
            if system_prompt:
                content = system_prompt + "\n\n" + content
                system_prompt = ""
            text += f"<start_of_turn>user\n{content}<end_of_turn>\n"
        elif msg["role"] in ["assistant", "model"]:
            text += f"<start_of_turn>model\n{msg['content']}<end_of_turn>\n"

    return tokenizer.bos_token + text.strip()

# ── Step 7: Training Config ────────────────────────────────────────────────────
training_args = SFTConfig(
    output_dir=OUTPUT_DIR,
    per_device_train_batch_size=1,
    gradient_accumulation_steps=8,          # keep effective batch ≈ 8 now that checkpointing is on
    optim="paged_adamw_8bit",               # halves optimizer state vs 32bit
    gradient_checkpointing=True,
    gradient_checkpointing_kwargs={"use_reentrant": False},
    max_seq_length=1024,                    # cap activations; raise only if you still have headroom
    packing=False,
    save_steps=50,
    save_total_limit=2,                     # don't fill the disk with checkpoints
    logging_steps=10,
    learning_rate=2e-4,
    fp16=False,
    bf16=True,
    max_grad_norm=0.3,
    max_steps=200,
    warmup_steps=10,
    lr_scheduler_type="constant",
    report_to="none",
)

trainer = SFTTrainer(
    model=model,
    train_dataset=dataset,
    formatting_func=formatting_prompts_func,
    processing_class=tokenizer,
    args=training_args,
)

# ── Step 8: Train ──────────────────────────────────────────────────────────────
print("Starting training...")
trainer.train()
print(f"Training complete! Saved to {OUTPUT_DIR}")

# ── Step 9: Reload and test ────────────────────────────────────────────────────
print("\nReloading fine-tuned model for inference test...")
del model
del trainer
gc.collect()
torch.cuda.empty_cache()

quantization_config = BitsAndBytesConfig(
    load_in_4bit=True,
    bnb_4bit_compute_dtype=torch.bfloat16,
    llm_int8_enable_fp32_cpu_offload=True,
)
base_model = AutoModelForCausalLM.from_pretrained(
    MODEL_ID,
    quantization_config=quantization_config,
    device_map="auto",
    max_memory={0: GPU_MEM_CAP, "cpu": CPU_MEM_CAP},
)
tokenizer = AutoTokenizer.from_pretrained(MODEL_ID)
model = PeftModel.from_pretrained(base_model, f"{OUTPUT_DIR}/checkpoint-200")
print("Fine-tuned model loaded!")

# 