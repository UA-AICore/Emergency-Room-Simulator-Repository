# from transformers import AutoTokenizer, AutoModelForCausalLM
# import torch

# model_id = "google/medgemma-27b-text-it"

# model = AutoModelForCausalLM.from_pretrained(
#     model_id,
#     dtype=torch.bfloat16,
#     device_map="auto",
# )
# tokenizer = AutoTokenizer.from_pretrained(model_id)

# messages = [
#     {
#         "role": "system",
#         "content": "You are a helpful medical assistant."
#     },
#     {
#         "role": "user",
#         "content": "How do you differentiate bacterial from viral pneumonia?"
#     }
# ]

# inputs = tokenizer.apply_chat_template(
#     messages,
#     add_generation_prompt=True,
#     tokenize=True,
#     return_dict=True,
#     return_tensors="pt",
# ).to(model.device)

# input_len = inputs["input_ids"].shape[-1]

# with torch.inference_mode():
#     generation = model.generate(**inputs, max_new_tokens=200, do_sample=False)
#     generation = generation[0][input_len:]

# decoded = tokenizer.decode(generation, skip_special_tokens=True)
# print(decoded)


# from ollama import chat

# response = chat(
#     model='alibayram/medgemma:27b',
#     messages=[{'role': 'user', 'content': 'Tell me about the human body.'}],
# )
# print(response.message.content)

import os

from transformers import AutoTokenizer

tokenizer = AutoTokenizer.from_pretrained(
    "google/medgemma-27b-text-it",
    token=os.environ.get("HF_TOKEN"),
    trust_remote_code=True,
)