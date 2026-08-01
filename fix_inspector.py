import re

with open("src/SsisLineage.Core/SsisPerformanceInspector.cs", "r", encoding="utf-8") as f:
    core_content = f.read()

core_content = re.sub(r'isEnglish\s*\?\s*"([^"]+)"\s*:\s*"[^"]+"', r'"\1"', core_content)

with open("src/SsisLineage.Core/SsisPerformanceInspector.cs", "w", encoding="utf-8") as f:
    f.write(core_content)
