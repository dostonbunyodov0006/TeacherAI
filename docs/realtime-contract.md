# Ustoz AI — real-time contract (v2)

Two layers:

1. **LLM → server:** Claude is constrained by structured outputs to the JSON schema in
   `Services/Mentor/MentorReply.cs` (`MentorJson.Schema()`, live at `GET /api/contract/schema` in Development).
2. **Server → browser:** SignalR hub `/hubs/ustoz` (JWT via `?access_token=`, time zone via `?tz=`). Every turn method
   is a **server-to-client stream** of `TurnEvent`s. Disposing the stream on the client cancels the turn (barge-in).

## 1. LLM reply (key order matters)

```jsonc
{
  // ── header: streamed first; the server forwards it as the "mentor" event before any audio exists
  "emotion": "neutral | laughing | mocking | shy_flustered | angry_flame | hurt_apologetic | thinking | teaching",
  "rageLevel": 0,                 // 0–10, server clamps ±4 per turn, persists it, cools 1 pt / 3 h
  "shyLevel": 0,                  // 0–10, only on praise; client fades it after the reply
  "userMood": "neutral | amused | hurt | frustrated | stressed",   // hurt/stressed ⇒ empathy override
  "eyeAndFaceCues": [ { "cue": "eye_roll", "segment": 1, "at": 0.3 } ],
      // wide_eyes squint bashful_look brow_furrow eye_roll head_nod head_shake faint_dramatic
      // + smirk slow_blink laugh_hard apologetic_soft curious_inspect thinking_look_up surprised_brow_raise
  "pivotToTopic": "Present Perfect: 3-mashq",                     // persisted as session.ActiveTopic
  // ── body
  "speechSegments": [
    { "type": "laugh_sfx", "emotion": "laughing", "text": "Xa-xa-xa!", "intensity": 3 },
    { "type": "speech",    "emotion": "mocking",  "text": "Sakkiz? Hozirgi darajang bilan beshga ham yetmaysan.", "intensity": 1 },
    { "type": "speech",    "emotion": "teaching", "text": "Bas, orzu yetar — Present Perfect, uchinchi mashq.", "intensity": 1 }
  ],                              // types: speech | laugh_sfx | sigh_sfx | gasp_sfx | scoff_sfx | pause
  // ── tail: parsed as each object closes, while audio is already playing
  "safety": { "category": "none", "note": "" },
  "imageRequest": { "action": "none", "prompt": "", "count": 1, "sourceImageIndex": 0 },
  "boardContent": { "show": false, "type": "grammar", "title": "", "markdownBody": "", "highlightWords": [] },
  "quiz": { "active": false, "question": "", "expectedAnswer": "", "timeLimitSeconds": 10 },
  "quizResult": { "graded": false, "correct": false, "feedback": "" },
  "mistakes": [], "homeworkUpdates": [], "assignHomework": [], "progressUpdates": [], "testResults": []
}
```

## 2. Hub methods (client → server)

| method | args | notes |
|---|---|---|
| `Ask` | `sessionId, wavBase64, options` | Space released. `options = { quizId, answerDelayMs, speechLanguage, attachmentIds[] }` |
| `AskText` | `sessionId, text, options` | typed turn |
| `QuizStarted` | `quizId` | countdown became visible → server clock starts |
| `QuizTimeout` | `sessionId, quizId` | countdown hit zero |
| `WeeklyRoast` | `sessionId` | Ustoz reads the weekly report in persona |

## 3. Turn events (server → client, in order)

| `type` | payload | client reaction |
|---|---|---|
| `state` | `state: "thinking"`, `sessionId` | instant "thinking" face (no dead air) |
| `transcript` | `text` | user bubble |
| `attachments` | `images[]` (signed URLs) | thumbnails in the user bubble |
| **`mentor`** | **`mentor: { emotion, rageLevel, shyLevel, userMood, eyeAndFaceCues[], pivotToTopic }`** | face, flame aura, blush and hearts, mood chip, topic pill. With empathy, the face softens *before* audio |
| `speech` | `index, text, segment: { index, type, emotion, intensity, firstOfSegment }, speech: { audioBase64?, audioMime, durationMs, visemes[] }` | strictly ordered playback. `audioMime: "sfx/laugh"` etc. = vocalization. Cues for `segment.index` fire at `at × durationMs` |
| `board` | `board` | whiteboard writes itself |
| `quiz` | `quiz: { id, question, timeLimitSeconds }` | armed; countdown starts when playback is idle. **No expected answer is sent** |
| `image_pending` / `images` / `image_filters` | prompt, signed URLs or presets | gallery on the board |
| `mistakes` / `quiz_result` | | transcript chips, quiz verdict |
| `policy` | `{ category, blockImages, empathyMode, angerCap, showSupportCard }` | support card (112 / 103) for self-harm |
| `done` | `sessionId, latencyMs, rageLevel` | final rage, refresh sidebar and profile |
| `error` | `text` | system message, idle |

## 4. Server guarantees (not left to the model)

- **Vocabulary filter** (`InsultFilter`), applied per speech segment **before TTS**:
  - `soft`: no insult words.
  - `strict`: only "jinnimisan" / "kasalmisan", and only at rage ≥ 7.
  - `savage` (opt-in): "ahmoq", "tupoy", "durak", "jinni", "kasal" at rage ≥ 6, and "dalbayob" / "dalban" at rage ≥ 8.
  - Empathy mode (hurt or stressed) masks everything, in every style.
- **Rage:** clamped ±4 per turn, and capped at 2 in empathy mode or 3 after a safety hit.
- **Safety hits:** block image jobs and are logged to `safety_events`.
- **Quiz answers** never leave the server. Answer timing is `min(client, server clock)`.
