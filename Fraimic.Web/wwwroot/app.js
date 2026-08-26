// Frame Muse front-end: submit a request, watch it generate, review the preview, send/discard,
// and manage the recent gallery. Talks only to the Fraimic.Web minimal API.
const $ = s => document.querySelector(s);
const promptEl = $('#prompt'), sendBtn = $('#send'), micBtn = $('#mic');
const statusEl = $('#status'), statusText = $('#statusText'), statusSub = $('#statusSub');
const dot = $('#dot'), fill = $('#fill'), preview = $('#preview'), previewImg = $('#previewImg'), grid = $('#grid');
const pvActions = $('#pvActions');
const styleEl = $('#style');
// Remember the chosen style between visits.
try { const saved = localStorage.getItem('fm_style'); if (saved) styleEl.value = saved; } catch {}
styleEl.addEventListener('change', () => { try { localStorage.setItem('fm_style', styleEl.value); } catch {} });
let previewId = null; // the job currently shown for review

// ---- Voice (browser Web Speech API; needs a secure context / HTTPS) ----
const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
const banner = $('#secureBanner'), secureLink = $('#secureLink');
// Microphone only works over HTTPS (or localhost). On http://frame.lan/ show the secure-link banner.
const micUsable = SR && window.isSecureContext;
if (!window.isSecureContext) {
  banner.style.display = 'block';
  secureLink.href = 'https://' + location.host + '/';
  secureLink.textContent = 'https://' + location.host + '/';
}
let recog = null, listening = false;
if (micUsable) {
  recog = new SR();
  recog.lang = 'en-US'; recog.interimResults = true; recog.continuous = false;
  let base = '';
  recog.onstart = () => { base = promptEl.value ? promptEl.value.trim() + ' ' : ''; };
  recog.onresult = e => {
    let t = '';
    for (let i = e.resultIndex; i < e.results.length; i++) t += e.results[i][0].transcript;
    promptEl.value = base + t;
  };
  recog.onend = () => { listening = false; micBtn.classList.remove('listening'); };
  recog.onerror = e => {
    listening = false; micBtn.classList.remove('listening');
    const msg = e.error === 'not-allowed' || e.error === 'service-not-allowed'
      ? 'Microphone blocked — allow mic access for this site in your browser settings.'
      : e.error === 'no-speech' ? "Didn't catch that — try again."
      : 'Voice error: ' + e.error;
    alert(msg);
  };
  micBtn.addEventListener('click', () => {
    if (listening) { recog.stop(); return; }
    try { recog.start(); listening = true; micBtn.classList.add('listening'); }
    catch { alert('Could not start the microphone.'); }
  });
} else {
  // No mic here — tapping it explains why rather than doing nothing.
  micBtn.addEventListener('click', () => {
    if (!window.isSecureContext) { banner.scrollIntoView({behavior:'smooth'}); alert('Voice needs the secure link. Open ' + secureLink.textContent + ' (see the banner up top).'); }
    else { alert('This browser does not support voice input — please type instead.'); }
  });
}

// ---- Reference photo (resized client-side, sent as base64) ----
const fileEl = $('#file'), attachBtn = $('#attach'), refpreview = $('#refpreview'), refimg = $('#refimg'), refremove = $('#refremove');
let refImageData = null;
attachBtn.addEventListener('click', () => fileEl.click());
fileEl.addEventListener('change', async () => {
  const f = fileEl.files[0]; if (!f) return;
  try {
    refImageData = await resizeImage(f, 1280, 0.85);
    refimg.src = refImageData; refpreview.style.display = 'flex';
  } catch { alert("Couldn't read that image."); }
});
refremove.addEventListener('click', () => { refImageData = null; fileEl.value = ''; refpreview.style.display = 'none'; });
function resizeImage(file, maxSide, quality) {
  return new Promise((res, rej) => {
    const img = new Image(), url = URL.createObjectURL(file);
    img.onload = () => {
      let w = img.width, h = img.height;
      if (Math.max(w, h) > maxSide) { const s = maxSide / Math.max(w, h); w = Math.round(w * s); h = Math.round(h * s); }
      const c = document.createElement('canvas'); c.width = w; c.height = h;
      c.getContext('2d').drawImage(img, 0, 0, w, h);
      URL.revokeObjectURL(url);
      res(c.toDataURL('image/jpeg', quality));
    };
    img.onerror = rej; img.src = url;
  });
}

// ---- Submit + poll ----
const STAGES = { Queued:8, Claimed:12, Enhancing:22, Generating:65, Encoding:80, Uploading:92, Preview:100, Done:100, Failed:100 };
const LABEL = { Queued:'In the queue…', Claimed:'Starting…', Enhancing:'Polishing your idea…',
  Generating:'Painting on the 5090…', Encoding:'Preparing for the frame…', Uploading:'Sending to the frame…',
  Preview:'Preview', Done:'Done — it\'s on the frame!', Failed:'Something went wrong.' };
let pollTimer = null;

function fmtEta(s){ if(s<=0) return ''; const m=Math.round(s/60); return m<=1 ? '~1 min' : `~${m} min`; }

async function submit() {
  const text = promptEl.value.trim();
  if (text.length < 2 && !refImageData) { promptEl.focus(); return; }  // need text OR a photo
  // Re-generating supersedes the current preview — drop it so it doesn't linger.
  if (previewId) { fetch('/api/jobs/' + previewId, { method:'DELETE' }).catch(()=>{}); }
  pvActions.classList.remove('show'); preview.style.display = 'none'; previewId = null;
  sendBtn.disabled = true; sendBtn.innerHTML = '<span class="spin"></span>Generating…';
  try {
    const r = await fetch('/api/jobs', {
      method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({ text, imageBase64: refImageData, style: styleEl.value })
    });
    if (!r.ok) { const e = await r.json().catch(()=>({})); throw new Error(e.error || 'Submit failed'); }
    const j = await r.json();
    showStatus('Queued', j.position, j.etaSeconds, null, null);
    poll(j.id);
  } catch (err) {
    showStatus('Failed', 0, 0, err.message, null);
    resetSend();
  }
}

function showStatus(status, position, eta, error, thumb) {
  statusEl.classList.add('show');
  dot.className = 'dot' + (status==='Done'||status==='Preview'?' done':status==='Failed'?' err':'');
  statusText.textContent = LABEL[status] || status;
  fill.style.width = (STAGES[status] ?? 0) + '%';
  fill.style.background = status === 'Failed' ? 'var(--err)' : '';  // else revert to the accent gradient
  if (status === 'Failed') { statusSub.textContent = error || ''; }
  else if (status === 'Queued' && position > 0) statusSub.textContent = `${position} ahead of you · ${fmtEta(eta)}`;
  else if (eta > 0) statusSub.textContent = fmtEta(eta) + ' remaining';
  else statusSub.textContent = '';
  if (thumb) { previewImg.src = thumb; preview.style.display='block'; }
}

function resetSend(){ sendBtn.disabled = false; sendBtn.textContent = 'Generate'; }

async function poll(id) {
  clearTimeout(pollTimer);
  try {
    const r = await fetch('/api/jobs/' + id);
    if (r.ok) {
      const s = await r.json();
      showStatus(s.status, s.position, s.etaSeconds, s.error, s.thumbnailDataUri);
      if (s.status === 'Preview') { resetSend(); showPreview(id, s.fullImageBase64, s.thumbnailDataUri); return; }
      if (s.status === 'Done') { resetSend(); loadRecent(); clearForm(); return; }
      if (s.status === 'Failed') { resetSend(); return; }
    }
  } catch {}
  pollTimer = setTimeout(() => poll(id), 3000);
}

function clearForm(){ promptEl.value=''; refImageData=null; fileEl.value=''; refpreview.style.display='none'; }

// ---- Preview review (Send to frame / Discard; the main button becomes "Try again") ----
function showPreview(id, fullB64, thumb) {
  previewId = id;
  previewImg.src = fullB64 ? ('data:image/jpeg;base64,' + fullB64) : (thumb || '');
  preview.style.display = 'block';
  pvActions.classList.add('show');
  statusSub.textContent = 'Not sent yet — tweak the text and hit Try again, or send it.';
  sendBtn.disabled = false; sendBtn.textContent = 'Try again';  // the box still has your prompt to edit
}

$('#pvSend').addEventListener('click', async () => {
  if (!previewId) return;
  const id = previewId;
  try {
    const r = await fetch('/api/jobs/' + id + '/send', { method:'POST' });
    if (!r.ok) { const e = await r.json().catch(()=>({})); throw new Error(e.error || 'failed'); }
    previewId = null;
    pvActions.classList.remove('show'); preview.style.display = 'none';
    dot.className = 'dot done'; statusText.textContent = 'Sent to the frame ✓'; fill.style.width = '100%';
    statusSub.textContent = 'It appears on the frame in a couple minutes.';
    showToast('Sent to the frame!');
    setTimeout(loadRecent, 4000); clearForm(); resetSend();
  } catch (e) { showToast(e.message); }
});
$('#pvDiscard').addEventListener('click', async () => {
  const id = previewId; previewId = null;
  pvActions.classList.remove('show'); preview.style.display='none'; statusEl.classList.remove('show');
  resetSend();
  if (id) { try { await fetch('/api/jobs/' + id, { method:'DELETE' }); } catch {} }
});

sendBtn.addEventListener('click', submit);
promptEl.addEventListener('keydown', e => { if ((e.metaKey||e.ctrlKey) && e.key==='Enter') submit(); });

// ---- Recent gallery (re-send / save / delete) ----
const toast = $('#toast'); let toastT = null;
function showToast(msg) { toast.textContent = msg; toast.classList.add('show'); clearTimeout(toastT); toastT = setTimeout(() => toast.classList.remove('show'), 2600); }

async function loadRecent() {
  try {
    const r = await fetch('/api/recent'); if (!r.ok) return;
    const items = await r.json();
    grid.innerHTML = '';
    for (const it of items) {
      if (!it.thumbnailDataUri) continue;
      const tile = document.createElement('div'); tile.className = 'tile'; tile.title = it.rawInput || '';
      const img = document.createElement('img'); img.src = it.thumbnailDataUri; img.alt = it.rawInput || '';
      const ops = document.createElement('div'); ops.className = 'ops';
      const send = document.createElement('button'); send.textContent = 'Frame';
      const save = document.createElement('a'); save.textContent = 'Save'; save.href = '/api/jobs/' + it.id + '/image'; save.setAttribute('download', 'frame-muse.jpg');
      const del = document.createElement('button'); del.className = 'del'; del.textContent = 'Del';
      send.addEventListener('click', () => resend(it.id, tile));
      del.addEventListener('click', () => remove(it.id, tile));
      ops.append(send, save, del); tile.append(img, ops); grid.appendChild(tile);
    }
  } catch {}
}

async function resend(id, tile) {
  tile.classList.add('busy');
  try {
    const r = await fetch('/api/jobs/' + id + '/resend', { method: 'POST' });
    if (!r.ok) { const e = await r.json().catch(()=>({})); throw new Error(e.error || 'failed'); }
    showToast('Sending to the frame…');
  } catch (e) { showToast(e.message); }
  finally { tile.classList.remove('busy'); }
}

async function remove(id, tile) {
  if (!confirm('Delete this image from the gallery?')) return;
  tile.classList.add('busy');
  try {
    const r = await fetch('/api/jobs/' + id, { method: 'DELETE' });
    if (!r.ok) throw new Error('delete failed');
    tile.remove(); showToast('Deleted.');
  } catch (e) { showToast(e.message); tile.classList.remove('busy'); }
}

loadRecent();
setInterval(loadRecent, 30000);
