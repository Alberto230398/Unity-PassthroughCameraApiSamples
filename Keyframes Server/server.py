"""
Keyframes Server
================
Riceve i keyframe inviati dal visore (Unity/HttpClient) via HTTP POST multipart
e li salva su disco, in una cartella per ogni keyframe.

Avvio:
    pip install -r requirements.txt
    uvicorn server:app --host 0.0.0.0 --port 8000

    --host 0.0.0.0  = accessibile dal visore tramite l'IP del PC in LAN
    --port 8000     = porta (cambiala se e' gia' occupata)

I file finiscono in:  <cartella di questo script>/keyframes/<index>/
Sfogliabili da browser in LAN:  http://<IP-del-PC>:8000/files/<index>/<nomefile>
"""

from datetime import datetime
from pathlib import Path

from fastapi import FastAPI, Request, UploadFile
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles

# --- Dove salvare -----------------------------------------------------------
# Cartella "keyframes" accanto a questo file. Cambia il percorso se vuoi
# salvare altrove, es:  BASE = Path("D:/MieiKeyframe")
BASE = Path(__file__).parent / "keyframes"
BASE.mkdir(parents=True, exist_ok=True)

app = FastAPI(title="Keyframes Server")


def log(msg: str) -> None:
    print(f"[{datetime.now():%H:%M:%S}] {msg}", flush=True)


# --- Test di connettivita' (accetta testo qualsiasi) ------------------------
@app.post("/ping")
async def ping(request: Request):
    """
    Endpoint di prova: accetta un corpo testuale qualsiasi e lo rimanda indietro.
    Serve a verificare la catena visore -> rete -> server senza mandare file veri.
    """
    body = (await request.body()).decode(errors="replace")
    log(f"ping da {request.client.host}: {body}")
    return {"ok": True, "ricevuto": body}


# --- Upload di un keyframe --------------------------------------------------
@app.post("/keyframe/{index}")
async def upload_keyframe(index: int, files: list[UploadFile]):
    """
    Riceve tutti i file di un keyframe e li scrive in keyframes/<index>/.
    Il campo del form deve chiamarsi "files" (uno per ogni file);
    il nome su disco e' quello che il client mette come filename.
    """
    dest = BASE / str(index)
    dest.mkdir(parents=True, exist_ok=True)

    saved = []
    total_bytes = 0
    for f in files:
        data = await f.read()
        (dest / f.filename).write_bytes(data)
        saved.append(f.filename)
        total_bytes += len(data)

    log(f"keyframe {index}: {len(saved)} file, {total_bytes / 1024:.0f} KB -> {dest}")
    return {"ok": True, "index": index, "saved": saved, "bytes": total_bytes}


# --- Utility di consultazione ----------------------------------------------
@app.get("/")
async def status():
    """Stato del server e conteggio dei keyframe salvati."""
    indices = sorted(
        (int(p.name) for p in BASE.iterdir() if p.is_dir() and p.name.isdigit())
    )
    total = sum(f.stat().st_size for f in BASE.rglob("*") if f.is_file())
    return {
        "status": "online",
        "storage": str(BASE.resolve()),
        "keyframes": len(indices),
        "last_index": indices[-1] if indices else None,
        "total_MB": round(total / 1024 / 1024, 1),
    }


@app.get("/keyframe/{index}")
async def list_keyframe(index: int):
    """Elenca i file di un keyframe."""
    dest = BASE / str(index)
    if not dest.is_dir():
        return JSONResponse({"error": "keyframe non trovato"}, status_code=404)
    return {
        "index": index,
        "files": [f.name for f in sorted(dest.iterdir()) if f.is_file()],
    }


# --- Download / anteprima da browser ---------------------------------------
# http://<IP>:8000/files/<index>/<nomefile>
app.mount("/files", StaticFiles(directory=BASE), name="files")


if __name__ == "__main__":
    import uvicorn

    log(f"Storage: {BASE.resolve()}")
    uvicorn.run(app, host="0.0.0.0", port=8000)
