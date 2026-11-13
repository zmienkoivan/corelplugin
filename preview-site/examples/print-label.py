"""Usage: python print-label.py label.pdf PERSISTED-JOB-UUID

Set PRINT_API_URL (HTTPS origin) and PRINT_API_KEY in the bot's protected environment.
Choose /api/print/v1/destination once. Persist UUID and destination per business action.
"""
import base64
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from uuid import UUID

base = os.environ['PRINT_API_URL'].rstrip('/')
assert base.startswith('https://')
job_id = str(UUID(sys.argv[2]))
def request(method, route, body=None):
    req = urllib.request.Request(base + '/api/print/v1/' + route, method=method,
        headers={'X-Print-Key': os.environ['PRINT_API_KEY'], 'Content-Type':'application/json'},
        data=json.dumps(body).encode() if body is not None else None)
    # No redirects: never forward the key to a different server.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args): return None
    with urllib.request.build_opener(NoRedirect()).open(req, timeout=40) as reply:
        return json.load(reply)

try:
    job = request('GET', 'jobs/' + job_id)
except urllib.error.HTTPError as error:
    if error.code != 404: raise
    pdf = Path(sys.argv[1]).read_bytes()
    fingerprint = hashlib.sha256(pdf).hexdigest()
    state_path = Path(sys.argv[1]).with_name('.print-' + job_id + '.json')
    if not state_path.exists():
        destination = request('GET','destination')['destination']
        if not destination: raise RuntimeError('Choose a printer first')
        try:
            with state_path.open('x',encoding='utf-8') as state_file:
                json.dump({'sha256':fingerprint,'destination':destination,'server':base},state_file)
        except FileExistsError:
            pass
    saved = json.loads(state_path.read_text(encoding='utf-8'))
    if saved['sha256'] != fingerprint or saved['server'] != base:
        raise RuntimeError('This print UUID already refers to another file or server')
    # A timeout leaves this state intact. Re-run with the same UUID to resolve it.
    job = request('POST','jobs',{'clientKey':job_id, **saved['destination'],
        'document':{'mimeType':'application/pdf','dataBase64':base64.b64encode(pdf).decode()}})
for _ in range(30):
    if job['status'] in ('submitted','failed','uncertain'): break
    time.sleep(1)
    job = request('GET','jobs/' + job_id)
print(json.dumps({'clientKey':job_id,'status':job['status'],'message':job.get('message'),'error':job.get('error')},ensure_ascii=False))
