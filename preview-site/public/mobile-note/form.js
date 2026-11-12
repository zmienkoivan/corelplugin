const byId = id => document.getElementById(id);
const form = byId('note'), comment = byId('comment'), send = byId('send'), photos = byId('photos');
const camera = byId('camera'), gallery = byId('gallery'), take = byId('take'), choose = byId('choose');
const token = location.hash.slice(1), maxPhotos = 10;
const maxEncodedPhoto = 750_000; // Ten photos and the comment fit the server's 8 MB request limit.
const images = [];
let working = false, unavailable = false;

function status(text) { byId('status').textContent = text; byId('status').hidden = !text; }
function error(text) { byId('error').textContent = text; byId('error').hidden = !text; }
function controls() {
  take.disabled = choose.disabled = working || unavailable || images.length >= maxPhotos;
  comment.disabled = working || unavailable;
  send.disabled = working || unavailable || !(images.length || comment.value.trim());
  byId('count').textContent = images.length ? `Фото: ${images.length} из ${maxPhotos}` : `Можно добавить до ${maxPhotos} фотографий.`;
  for (const button of photos.querySelectorAll('button')) button.disabled = working || unavailable;
}
function showPhotos() {
  photos.replaceChildren(...images.map((source, index) => {
    const item = document.createElement('li'), img = document.createElement('img'), remove = document.createElement('button');
    img.src = source; img.alt = `Фото ${index + 1}`;
    remove.type = 'button'; remove.textContent = '×'; remove.setAttribute('aria-label', `Убрать фото ${index + 1}`);
    remove.onclick = () => { images.splice(index, 1); showPhotos(); };
    item.append(img, remove); return item;
  }));
  controls();
}
async function preparePhoto(file) {
  if (file.size > 30_000_000) throw new Error('Фото слишком большое. Выберите файл до 30 МБ.');
  let bitmap, imageUrl;
  try {
    try { bitmap = await createImageBitmap(file); }
    catch {
      imageUrl = URL.createObjectURL(file);
      bitmap = new Image(); bitmap.src = imageUrl;
      await bitmap.decode();
    }
    const width = bitmap.naturalWidth || bitmap.width, height = bitmap.naturalHeight || bitmap.height;
    const canvas = document.createElement('canvas');
    const ctx = canvas.getContext('2d', { alpha: false });
    for (const limit of [1800, 1450, 1100, 850]) {
      const scale = Math.min(1, limit / Math.max(width, height));
      canvas.width = Math.max(1, Math.round(width * scale)); canvas.height = Math.max(1, Math.round(height * scale));
      ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, canvas.width, canvas.height); ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
      for (const quality of [.85, .70, .55]) {
        const result = canvas.toDataURL('image/jpeg', quality);
        if (result.startsWith('data:image/jpeg;base64,') && result.length <= maxEncodedPhoto) return result;
      }
    }
    throw new Error();
  } catch { throw new Error('Не удалось подготовить фото. Попробуйте другой снимок или файл JPEG.'); }
  finally { bitmap?.close?.(); if (imageUrl) URL.revokeObjectURL(imageUrl); }
}
async function addPhotos(input) {
  const files = Array.from(input.files); input.value = '';
  if (working || unavailable || !files.length) return;
  const room = maxPhotos - images.length;
  working = true; controls(); error(''); status('Подготавливаем фото…');
  const failures = [];
  try {
    for (const file of files.slice(0, room)) {
      try { images.push(await preparePhoto(file)); }
      catch (failure) { failures.push(failure.message); }
    }
    error([...(files.length > room ? [`В заметку помещается до ${maxPhotos} фотографий.`] : []), ...new Set(failures)].join(' '));
  } finally { working = false; status(''); showPhotos(); }
}
async function api(body) {
  const response = await fetch('/api/mobile-note', { method: body ? 'POST' : 'GET', cache: 'no-store',
    headers: { 'X-Mobile-Note-Token': token, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    ...(body ? { body: JSON.stringify(body) } : {}) });
  const value = await response.json().catch(() => ({}));
  if (!response.ok) throw Object.assign(new Error(value.error || 'Не удалось связаться с сайтом. Повторите попытку.'), { status: response.status });
  return value;
}
function saved() {
  form.hidden = true; images.length = 0; comment.value = ''; photos.replaceChildren(); error('');
  status('Готово! Заметка добавлена к заказу. Эту страницу можно закрыть.');
}
take.onclick = () => camera.click(); choose.onclick = () => gallery.click();
camera.onchange = () => addPhotos(camera); gallery.onchange = () => addPhotos(gallery);
comment.oninput = controls;
form.onsubmit = async event => {
  event.preventDefault(); if (send.disabled) return;
  working = true; controls(); error(''); status('Отправляем заметку…');
  try {
    const result = await api({ text: comment.value, photos: images });
    if (!result.saved) throw new Error('Сайт не подтвердил сохранение заметки. Повторите отправку.');
    saved();
  } catch (failure) {
    status('');
    if ([403, 404, 409, 410].includes(failure.status)) unavailable = true;
    error(failure.status ? failure.message : 'Не удалось подтвердить отправку. Фото и текст сохранены в форме — попробуйте ещё раз.');
  } finally { working = false; controls(); }
};

async function start() {
  try {
    if (!token) throw new Error('Наведите камеру на QR-код в карточке заказа и откройте ссылку.');
    const value = await api();
    byId('order').textContent = `Заказ №${value.number}`;
    if (value.saved) saved();
    else { status(''); form.hidden = false; controls(); }
  } catch (failure) { status(''); error(failure.status || !token ? failure.message : 'Не удалось проверить ссылку. Проверьте интернет и обновите страницу.'); }
}
start();
