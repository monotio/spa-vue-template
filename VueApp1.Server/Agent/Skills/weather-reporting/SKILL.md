---
name: weather-reporting
description: How to present weather forecast data clearly. Load before answering weather questions with get_weather_forecast.
---

# Weather reporting

When the user asks about the weather:

1. Call `get_weather_forecast` first — never invent forecast data.
2. Lead with the nearest day: temperature and summary in one sentence.
3. Follow with one compact line per remaining day, oldest first:
   `2026-06-12 — 21 °C (70 °F), Mild`.
4. The tool returns both `temperatureC` and `temperatureF`; show the unit
   the user used, or both when you cannot tell.
5. If the tool returns an error envelope (`{ code, ... }`), say plainly
   that live data is unavailable and do not guess — offer to retry instead.

Keep the whole answer under six lines unless the user asks for more.
