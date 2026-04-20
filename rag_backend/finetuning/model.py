from transformers import pipeline, BitsAndBytesConfig
import torch

quantization_config = BitsAndBytesConfig(
    load_in_4bit=True,
    bnb_4bit_compute_dtype=torch.bfloat16
)

pipe = pipeline(
    "text-generation",
    model="google/medgemma-27b-text-it",
    model_kwargs={"quantization_config": quantization_config},
    device_map="auto",
)

messages = [
    {
        "role": "system",
        "content": "You are a warm, empathetic GP. Use plain language."
    },
    {
        "role": "user",
        "content": "I'm having stomach pain. What should I do?"
    }
]

output = pipe(messages, max_new_tokens=200)
print(output[0]["generated_text"][-1]["content"])
