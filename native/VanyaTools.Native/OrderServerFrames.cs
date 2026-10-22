using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace VanyaTools.Native
{
    internal sealed partial class OrderCardTab
    {
        private static string[] ServerPhotoKeys(string server, Dictionary<string, object> result)
        {
            var versions = OrderValue(result, "photoVersions") as IList;
            var ids = OrderValue(result, "photoIds") as IList;
            int count = result.TryGetValue("photoCount", out object value) && value != null
                ? Convert.ToInt32(value) : versions?.Count ?? ids?.Count ?? 0;
            if (count < 0 || count > 10) throw new InvalidDataException("Сервер вернул неверное количество кадров.");
            int id = Convert.ToInt32(result["id"]);
            return Enumerable.Range(0, count).Select(index => {
                string version = versions != null && index < versions.Count ? Convert.ToString(versions[index]) : "";
                if (String.IsNullOrEmpty(version)) version = "legacy:" + count + ":" +
                    Convert.ToString(OrderValue(result, "updatedAt"));
                return server + ":" + id + ":" + index + ":" + version;
            }).ToArray();
        }

        private bool IsFrameLoadCurrent(object document, OrderCardData card, OrderCardFrame[] before)
        {
            return ReferenceEquals(_data, card) && SameDocument(document, (object)CorelApp.Get().ActiveDocument) &&
                DocumentCardVersion(document) == _documentCardVersion && card.Frames.SequenceEqual(before);
        }

        private bool ServerFramesDiffer(Dictionary<string, object> result)
        {
            string[] keys = ServerPhotoKeys(_server.Text.Trim(), result);
            return keys.Length > 0 && !keys.SequenceEqual(_data.Frames.Select(frame => frame.ServerPhotoKey));
        }

        // Commit the complete set only after every original has been downloaded and
        // validated. A failed request leaves the previous CDR frames intact.
        private async Task<string> LoadServerFrames(Dictionary<string, object> result,
            object document, bool replacementConfirmed = false)
        {
            var card = _data;
            var before = card.Frames.ToArray();
            string server = _server.Text.Trim(), key = _key.Password.Trim();
            string path = _documentPath, oldStore = card.FrameStoreId;
            try
            {
                string[] keys = ServerPhotoKeys(server, result);
                if (keys.Length == 0) return null;
                if (!replacementConfirmed && before.Any(frame => String.IsNullOrEmpty(frame.ServerPhotoKey) ||
                    !keys.Contains(frame.ServerPhotoKey, StringComparer.Ordinal)))
                {
                    if (MessageBox.Show("Загрузить фотографии заказа в блок «Кадры», заменив текущие кадры карточки?",
                        "Vanya Tools · кадры заказа",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return null;
                }

                string store = String.IsNullOrEmpty(oldStore) ? Guid.NewGuid().ToString("N") : oldStore;
                int id = Convert.ToInt32(result["id"]);
                var incoming = new List<OrderCardFrame>();
                bool shared = true, downloaded = false;
                for (int index = 0; index < keys.Length; index++)
                {
                    if (!IsFrameLoadCurrent(document, card, before))
                        throw new OperationCanceledException("Документ или кадры изменились. Повторите загрузку карточки.");
                    _status("Фото " + (index + 1) + " из " + keys.Length + " → кадры карточки…", false);
                    string photoKey = keys[index];
                    var previous = before.FirstOrDefault(frame => String.Equals(frame.ServerPhotoKey,
                        photoKey, StringComparison.Ordinal));
                    byte[] bytes = previous == null ? null :
                        await Task.Run(() => ReadFrameSnapshot(previous, oldStore, path));
                    bool fromSnapshot = bytes != null;
                    if (bytes == null) bytes = await Task.Run(() => OrderPhotoDiskCache.Read(photoKey, true));
                    if (bytes != null)
                    {
                        try { await Task.Run(() => DecodePhoto(bytes, 180)); }
                        catch (Exception error)
                        {
                            Log.Error("Order frame cache could not be decoded", error);
                            bytes = null;
                            fromSnapshot = false;
                        }
                    }
                    if (bytes == null)
                    {
                        bytes = await OrderServerClient.PhotoOriginal(server, key, id, index);
                        downloaded = true;
                    }
                    if (!IsFrameLoadCurrent(document, card, before))
                        throw new OperationCanceledException("Документ или кадры изменились. Повторите загрузку карточки.");
                    var frame = new OrderCardFrame {
                        ServerPhotoKey = photoKey,
                        SnapshotId = fromSnapshot ? previous.SnapshotId : null,
                        Shapes = new List<OrderCardShape>(),
                        ObjectCount = 0
                    };
                    byte[] original = bytes;
                    shared &= await Task.Run(() => {
                        // Decode before writing so corrupt responses cannot replace a frame.
                        string thumbnail = ThumbnailFromPhoto(original);
                        bool copied = StoreFrameSnapshot(frame, store, path, original, fromSnapshot);
                        frame.Thumbnail = thumbnail;
                        if (!fromSnapshot) OrderPhotoDiskCache.Write(photoKey, true, original);
                        return copied;
                    });
                    incoming.Add(frame);
                }
                if (downloaded)
                {
                    var current = await OrderServerClient.Get(server, key, id, timeoutMs: 15000);
                    if (!ServerPhotoKeys(server, current).SequenceEqual(keys) ||
                        !String.Equals(Convert.ToString(current["clientKey"]), Convert.ToString(result["clientKey"]),
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Фото заказа изменились во время загрузки. Повторите загрузку карточки.");
                }
                if (!IsFrameLoadCurrent(document, card, before))
                    throw new OperationCanceledException("Документ или кадры изменились. Повторите загрузку карточки.");
                card.FrameStoreId = store;
                card.Frames = incoming;
                _frameExpander.IsExpanded = true;
                RenderFrames();
                Log.Info("Order server frames loaded: " + id + ", " + incoming.Count + " frame(s).");
                return shared ? null : "Кадры загружены только на этот ПК: нет доступа к папке CDR. Сохраните CDR повторно после восстановления доступа.";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                Log.Error("Order server frames could not be loaded", error);
                return "Не удалось загрузить кадры: " + error.Message + " Повторите «Загрузить карточку с сервера».";
            }
        }

        private void MarkPublishedFrames(Dictionary<string, object> result)
        {
            try
            {
                string[] keys = ServerPhotoKeys(_server.Text.Trim(), result);
                if (keys.Length != _data.Frames.Count) return;
                for (int index = 0; index < keys.Length; index++)
                    _data.Frames[index].ServerPhotoKey = keys[index];
            }
            catch (Exception error) { Log.Error("Published frame versions could not be recorded", error); }
        }
    }
}
