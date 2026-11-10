"""Guard module dependencies and keep provider/transport details out of application/domain code."""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1] / "back" / "Modules"
violations = []
for module in root.iterdir():
    if not module.is_dir():
        continue
    for source in module.rglob("*.cs"):
        text = source.read_text(encoding="utf-8-sig")
        for dependency, suffix in re.findall(r"DocParse\.Modules\.(\w+)((?:\.\w+)*)", text):
            if dependency != module.name and not suffix.startswith(".Contracts"):
                violations.append(f"{source.relative_to(root)} crosses into {dependency}{suffix}")
        if {"Application", "Domain"}.intersection(source.relative_to(module).parts):
            if re.search(r"using (Azure\.|Pgvector|OfficeOpenXml|CsvHelper|Microsoft\.EntityFrameworkCore|Microsoft\.AspNetCore|.*\.Infrastructure)", text):
                violations.append(f"{source.relative_to(root)} depends on transport/provider/persistence details")
            if "IFormFile" in text:
                violations.append(f"{source.relative_to(root)} depends on HTTP file input")
assert not violations, "\n".join(violations)
print("PASS: module contracts and application/domain dependency boundaries.")
