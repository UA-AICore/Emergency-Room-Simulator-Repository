# HeyGen Avatar AI Personality Override Issue

## Problem

HeyGen Interactive Avatars have **built-in conversational AI** that processes text through their own LLM. When we send text via `streaming.task`, the avatar's AI interprets and responds to the text rather than speaking it verbatim.

This means:
- ✅ Text IS being sent to HeyGen successfully
- ❌ HeyGen's AI processes the text and generates its own response
- ❌ Our RAG+personality output is being ignored/reinterpreted

## Root Cause

HeyGen Interactive Avatars (`*_public` avatars) are designed for **conversational AI interactions**, not verbatim text-to-speech. The `streaming.task` API with `task_type: "talk"` sends text to the avatar's AI system, which then generates a response.

## Current Implementation

1. **RAG+Personality Layer** generates: "Great question! Let me teach you about trauma care..."
2. **We send to HeyGen**: The exact text from RAG+personality
3. **HeyGen's AI processes it**: Interprets it as a prompt and generates its own response
4. **Avatar speaks**: HeyGen's AI-generated response, not our text

## Potential Solutions

### Solution 1: Format Text as Direct Response (Current Attempt)
**Status**: Testing
**Approach**: Prefix text with `[SPEAK EXACTLY AS WRITTEN]` to instruct HeyGen's AI to speak verbatim.

```csharp
var textToSend = $"[SPEAK EXACTLY AS WRITTEN] {text}";
```

**Limitation**: May not work if HeyGen's AI ignores the instruction.

### Solution 2: Use System Prompt/Personality Override
**Status**: Needs Research
**Approach**: Check HeyGen API for `system_prompt`, `personality`, or similar parameters in `streaming.new` request.

**Action Items**:
- Review HeyGen API documentation for session configuration options
- Test if we can override the avatar's default personality/system prompt
- Set system prompt to: "You are a text-to-speech avatar. Speak all text exactly as provided without interpretation."

### Solution 3: Use Non-Interactive Avatar
**Status**: Needs Investigation
**Approach**: Use a HeyGen avatar type that doesn't have conversational AI (if available).

**Action Items**:
- Check if HeyGen offers "TTS-only" avatars without AI processing
- Verify if different avatar types support verbatim speech

### Solution 4: SSML or Special Formatting
**Status**: Needs Research
**Approach**: Use SSML (Speech Synthesis Markup Language) or HeyGen-specific formatting to force verbatim speech.

**Action Items**:
- Check if HeyGen supports SSML in `streaming.task` text parameter
- Test special formatting markers that might disable AI processing

### Solution 5: Use Different API Endpoint
**Status**: Needs Investigation
**Approach**: Check if HeyGen has a different API endpoint for verbatim text-to-speech (not conversational AI).

**Action Items**:
- Review HeyGen API documentation for TTS-specific endpoints
- Check if `streaming.task` has other `task_type` values besides "talk"

## Recommended Next Steps

1. **Test Current Solution**: Test the `[SPEAK EXACTLY AS WRITTEN]` prefix to see if it helps
2. **Review HeyGen API Docs**: Check official documentation for:
   - System prompt/personality override options
   - SSML support
   - Alternative endpoints for verbatim speech
   - Avatar configuration options
3. **Contact HeyGen Support**: Ask about disabling AI processing for verbatim text-to-speech
4. **Consider Alternative**: If HeyGen doesn't support verbatim speech, consider:
   - Using a different avatar service that supports TTS
   - Using HeyGen's video generation API (not streaming) for pre-rendered responses
   - Accepting that the avatar will have its own personality and work with it

## Code Changes Made

1. **Added prefix to text**: `[SPEAK EXACTLY AS WRITTEN]` before sending to HeyGen
2. **Enhanced logging**: Logs formatted text being sent to HeyGen
3. **Added TODO comment**: In `CreateStreamingSessionAsync()` to investigate system prompt options

## Testing

To test if the current solution works:
1. Send a message through the avatar
2. Check backend logs for "Sending formatted text to HeyGen"
3. Compare what the avatar says vs. what was sent
4. If avatar still ignores our text, try other solutions


