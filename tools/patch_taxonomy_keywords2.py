# -*- coding: utf-8 -*-
import json
from pathlib import Path

PATH = Path("backend/Zakupki.Categorization/Taxonomy/taxonomy.v2.json")
data = json.loads(PATH.read_text(encoding="utf-8"))
by_id = {c["id"]: c for g in data["categories"] for c in g["children"]}

# Tighten noisy short tokens / ministry false friends.
by_id["g06c16"]["keywords"] = [
    "продукты питания",
    "продуктов питания",
    "бакалея",
    "поставка продуктов",
    "набор продуктов",
]
by_id["g13c04"]["keywords"] = [
    "метизы",
    "крепеж",
    "болты и гайки",
    "саморезы",
    "анкерные болты",
    "сварочные электроды",
]
by_id["g09c04"]["keywords"] = [
    "ответственное хранение",
    "складские услуги",
    "услуг хранения",
    "складирование",
]
by_id["g05c02"]["keywords"] = [
    "медицинские изделия",
    "медицинских изделий",
    "расходные материалы медицин",
    "перчатки медицин",
    "шприцы",
    "бинты",
    "катетер",
    "перевязочные материалы",
    "имплантаты",
    "шприц-колба",
    "ангиографическ",
]
# VipNet / crypto gear
by_id["g07c03"]["keywords"] = list(dict.fromkeys((by_id["g07c03"].get("keywords") or []) + [
    "VipNet",
    "випнет",
    "СЗИ",
    "средства защиты информации",
    "криптошлюз",
]))
by_id["g07c01"]["keywords"] = list(dict.fromkeys((by_id["g07c01"].get("keywords") or []) + [
    "компьютерное оборудование",
    "серверное оборудование",
    "ИТ оборудование",
    "вычислительная техника",
]))

# Ecology disposal
by_id["g24c06"]["keywords"] = list(dict.fromkeys((by_id["g24c06"].get("keywords") or []) + [
    "утилизация тары",
    "утилизация отходов",
    "обезвреживание",
    "захоронение отходов",
]))

PATH.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print("ok")
