# -*- coding: utf-8 -*-
"""Enrich / harden taxonomy.v2 keywords based on audit failures."""
from __future__ import annotations

import json
from pathlib import Path

PATH = Path("backend/Zakupki.Categorization/Taxonomy/taxonomy.v2.json")

EXTRA = {
    "g05c01": [
        "лекарственный препарат",
        "лекарственных препаратов",
        "лекарственного препарата",
        "медикамент",
        "фармацевтическ",
        "инъекц",
    ],
    "g05c02": [
        "медицинские изделия",
        "медицинских изделий",
        "расходные материалы",
        "шприц-колба",
        "ангиографическ",
    ],
    "g05c04": [
        "реагент",
        "реагентов",
        "для анализатора",
        "лабораторн",
        "иммуноферментн",
        "гематологическ",
        "гликированного гемоглобина",
    ],
    "g06c17": [
        "продуктов питания",
        "продукты питания",
        "продукт питания",
        "пищев",
    ],
    "g23c14": [  # may not exist; filtered below
        "ветеринар",
        "гербицид",
    ],
}

# Soften overly broad single-token triggers that fire from document boilerplate.
REPLACE = {
    "g07c04": [
        "разработка программного обеспечения",
        "сопровождение программного обеспечения",
        "доработка программного обеспечения",
        "информационная система",
        "автоматизированная информационная система",
        "АИС",
    ],
    "g07c11": [
        "телефония",
        "сотовая связь",
        "мобильная связь",
        "радиосвязь",
        "услуги связи",
        "телефонная связь",
        "рации",
    ],
    "g18c12": [
        "страхование",
        "ОСАГО",
        "страховой полис",
        "страхование имущества",
        "договор страхования",
    ],
    "g18c11": [
        "кредитн",
        "лизинг",
        "финансовая аренда",
        "заём",
        "займ",
    ],
    "g14c06": [
        "тара и упаковка",
        "картонная коробка",
        "полиэтиленовый пакет",
        "этикетка",
        "бочки",
        "мешки",
    ],
    "g01c01": [
        "строительство зданий",
        "строительство объекта",
        "возведение",
        "новое строительство",
        "объект капитального строительства",
        "строительно-монтажные работы",
    ],
}


def main() -> None:
    data = json.loads(PATH.read_text(encoding="utf-8"))
    by_id = {}
    for g in data["categories"]:
        for c in g["children"]:
            by_id[c["id"]] = c

    changed = []
    for cid, kws in EXTRA.items():
        if cid not in by_id:
            continue
        cur = by_id[cid].setdefault("keywords", [])
        before = len(cur)
        for kw in kws:
            if kw not in cur:
                cur.append(kw)
        if len(cur) != before:
            changed.append(cid)

    for cid, kws in REPLACE.items():
        if cid not in by_id:
            continue
        by_id[cid]["keywords"] = kws
        changed.append(cid)

    # Find veterinary category if present
    for cid, c in by_id.items():
        name = (c.get("name") or "").lower()
        if "ветеринар" in name:
            cur = c.setdefault("keywords", [])
            for kw in ["ветеринар", "ветпрепарат", "гербицид"]:
                if kw not in cur:
                    cur.append(kw)
            changed.append(cid)

    PATH.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print("patched", sorted(set(changed)))


if __name__ == "__main__":
    main()
