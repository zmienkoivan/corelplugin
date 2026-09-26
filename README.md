# CorelDRAW workspace macros

Рабочий набор макросов и докер-панель для автоматизации повторяющихся операций в CorelDRAW.

## Новый основной путь: native addon без `.gms`

Чтобы установка была солидной и переносимой на другие ПК, проект переводится на C#/.NET addon:

- `native/VanyaTools.Native` - WPF docker control и логика команд.
- `addon/VanyaToolsNative` - CorelDRAW addon, который загружает `VanyaTools.Native.dll`.
- `build-native.ps1` - сборка DLL.
- `install-native.ps1` - установка в CorelDRAW `Programs64\Addons`.

Сборка и установка:

```powershell
.\install-native.ps1 -Build
```

Если установка в `Program Files` ругается на права, запустить PowerShell от имени администратора и повторить команду.

Собрать переносимый установочный пакет для другого ПК:

```powershell
.\package-native.ps1
```

Архив появится здесь:

```text
release/VanyaToolsNative.zip
```

На целевом компьютере нужны Windows x64 и установленный CorelDRAW x64. Распакуйте ZIP целиком и запустите `INSTALL.bat`. Установщик найдёт папку CorelDRAW, предложит выбрать версию, если их несколько, и запросит права Windows только при необходимости. Если программа установлена в нестандартное место, установщик попросит путь к `Programs64` или `Programs64\Addons`.

После установки перезапустите CorelDRAW и откройте `Окно > Окна настройки (Dockers) > Vanya Tools Native`.

### Обновления с GitHub

Пакет содержит updater для публичного репозитория `zmienkoivan/corelplugin`. После установки ярлык «Проверить обновления» появится в меню Пуск в папке `Vanya Tools`. Он получает последний опубликованный GitHub Release с готовым `VanyaToolsNative.zip` и переустанавливает аддон. Перед обновлением нужно закрыть CorelDRAW. Git на компьютерах пользователей не нужен.

Workflow `.github/workflows/release.yml` собирает аддон и публикует ZIP при отправке тега версии, например:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

После публикации релиза пользователи запускают «Проверить обновления» из меню Пуск. Репозиторий должен быть публичным для работы updater без токена.

После перезапуска CorelDRAW панель появится здесь:

```text
Window > Dockers > Vanya Tools Native
```

Старый `.gms`/HTML вариант оставлен как прототип и запасной путь.

## Первая функция: trim bitmap

`src/BitmapTrim.bas` содержит макросы для обрезки прозрачной рамки у выбранных растровых изображений, аналогично Photoshop Trim по прозрачным пикселям.

Основной сценарий:

1. Выделить один или несколько PNG/bitmap объектов с прозрачностью.
2. Запустить `BitmapTrim.TrimSelectedBitmaps`.
3. Макрос найдет границы видимых пикселей по alpha channel и применит crop bitmap.

Настройки в начале модуля:

- `DEFAULT_ALPHA_THRESHOLD` - пиксели с alpha выше этого значения считаются видимыми.
- `DEFAULT_PADDING_PX` - дополнительный отступ в пикселях после тримминга.

## Вторая функция: contour cut для стикерпаков

`src/StickerCut.bas` автоматизирует контурный рез по выделенному стикерпаку.

Что делает макрос:

1. Дублирует выделенные объекты, не трогая оригинал.
2. Растрирует копию с прозрачным фоном.
3. Трассирует временный растр.
4. Создает общий внешний `Boundary`.
5. Оставляет внешние замкнутые линии, а внутренние пути (отверстия) удаляет по геометрическому вложению и направлению кривой.
6. Строит внешний offset в миллиметрах с круглыми углами и упрощает лишние узлы с заданным допуском.
7. Удаляет временные объекты и оставляет линии реза.

Основные команды:

- `StickerCut.CreateStickerCutContour` - спросить offset в мм.
- `StickerCut.CreateStickerCutContour1mm` - контур 1 мм.
- `StickerCut.CreateStickerCutContour2mm` - контур 2 мм.
- `StickerCut.CreateStickerCutContour3mm` - контур 3 мм.

В нативной панели можно настроить допуск упрощения контура в миллиметрах и размеры язычка: ширину, длину и радиус скругления.

Настройки в начале модуля:

- `DEFAULT_CUT_OFFSET_MM` - отступ по умолчанию.
- `DEFAULT_RASTER_DPI` - разрешение временной растризации.
- `DEFAULT_TRACE_SMOOTHING` - сглаживание трассировки.
- `DEFAULT_TRACE_DETAIL` - детализация трассировки.
- `DEFAULT_CUT_LINE_WIDTH_MM` - толщина линии готового контура.

## Как поставить в CorelDRAW

### 1. Создать VBA project

`.gms` бинарник нужно создать внутри CorelDRAW:

1. Открыть `Tools > Scripts > Scripts`.
2. Создать новый VBA macro project, например `VanyaTools.gms`.
3. Открыть VBA editor.
4. Импортировать `src/BitmapTrim.bas`.
5. Импортировать `src/StickerCut.bas`.
6. Запустить нужный макрос из `BitmapTrim` или `StickerCut`.

### 2. Поставить полноценную боковую панель

Готовый каркас CorelDRAW docker лежит в `addon/VanyaTools`.

После создания `VanyaTools.gms` скопировать его в эту папку:

```text
addon/VanyaTools/VanyaTools.gms
```

Затем скопировать всю папку:

```text
addon/VanyaTools
```

в CorelDRAW Addons folder, например:

```text
C:\Program Files\Corel\CorelDRAW Graphics Suite <version>\Programs64\Addons\VanyaTools
```

После перезапуска CorelDRAW панель должна появиться здесь:

```text
Window > Dockers > Vanya Tools
```

Внутри панели уже есть настройки contour cut: offset в мм, DPI временной растризации, smoothing и detail для трассировки.

### Быстрая установка скриптом

После создания `VanyaTools.gms` можно установить addon одной командой:

```powershell
.\install.ps1 -GmsPath "C:\path\to\VanyaTools.gms"
```

Если `VanyaTools.gms` уже лежит в `addon/VanyaTools/VanyaTools.gms`, достаточно:

```powershell
.\install.ps1
```

Если `.gms` еще не создан, скрипт все равно поставит боковую панель, но кнопки макросов начнут работать только после повторной установки с `-GmsPath`.

Строгий режим, который останавливается без `.gms`:

```powershell
.\install.ps1 -RequireGms
```

Если CorelDRAW установлен нестандартно:

```powershell
.\install.ps1 -CorelAddonsPath "C:\Program Files\Corel\CorelDRAW Graphics Suite 2024\Programs64\Addons"
```

Скрипт синхронизирует HTML-панель, копирует addon в папку CorelDRAW и не удаляет другие addon-папки.

## Источники API

- CorelDRAW поддерживает VBA macro projects в `.gms` и загрузку через Scripts docker.
- Custom dockers могут быть HTML-панелями, которые вызывают объектную модель через `window.external.Application`.
- `Bitmap.ImageAlpha`, `Bitmap.CropEnvelope` и `Bitmap.Crop` используются для работы с прозрачным bitmap и постоянной обрезкой.
- `ShapeRange.ConvertToBitmapEx` создаёт растр с прозрачностью; внешний контур строится напрямую по альфа-каналу. Для растра без прозрачности используется `Bitmap.Trace`; затем применяются `ShapeRange.CreateBoundary` и `Curve.Contour`.
