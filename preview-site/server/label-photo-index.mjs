export function labelPhotoIndex(value) {
  const index = value ?? 0;
  if (!Number.isSafeInteger(index) || index < 0 || index > 99)
    throw new Error('Неверный номер фото для этикетки.');
  return index;
}
