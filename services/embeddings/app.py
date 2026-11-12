from __future__ import annotations

import os
from typing import Any

from fastapi import FastAPI, HTTPException
from fastembed import TextEmbedding
from pydantic import BaseModel, Field

MODEL_NAME = os.environ.get(
    "EMBEDDING_MODEL", "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"
)

app = FastAPI(title="Zakupki Embeddings", version="0.1.0")
model = TextEmbedding(model_name=MODEL_NAME)
# Probe one vector to learn dimensions at startup.
_probe = next(model.embed(["dimension probe"]))
EMBEDDING_DIM = len(_probe)


class EmbedRequest(BaseModel):
    texts: list[str] = Field(min_length=1)


class EmbedResponse(BaseModel):
    model: str
    dimensions: int
    embeddings: list[list[float]]


@app.get("/health")
def health() -> dict[str, Any]:
    return {
        "status": "ok",
        "model": MODEL_NAME,
        "dimensions": EMBEDDING_DIM,
    }


@app.post("/embed", response_model=EmbedResponse)
def embed(request: EmbedRequest) -> EmbedResponse:
    cleaned = [t.strip() for t in request.texts if t and t.strip()]
    if not cleaned:
        raise HTTPException(status_code=400, detail="texts must contain non-empty strings")

    vectors = [vec.tolist() for vec in model.embed(cleaned)]
    return EmbedResponse(
        model=MODEL_NAME,
        dimensions=EMBEDDING_DIM,
        embeddings=vectors,
    )
