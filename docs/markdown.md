# Экспорт PDF в Markdown

Экспорт строит один `document.md` и, когда в логической структуре встречаются растровые
`Figure`, каталог sidecar-изображений. Рендеринг сначала происходит целиком в памяти и не меняет
исходный `PdfDocument`.

```csharp
using Inkwright;
using Inkwright.Markdown;

using PdfDocument pdf = PdfDocument.Load("report.pdf");

MarkdownDocument markdown = pdf.ToMarkdown();
Console.WriteLine(markdown.Text);

foreach (MarkdownExportWarning warning in markdown.Diagnostics)
    Console.WriteLine(warning);

await markdown.SaveAsync("export");
// export/document.md
// export/media/image1.png, когда он упомянут в Markdown
```

Короткая форма делает тот же in-memory проход и затем сохраняет результат:

```csharp
await pdf.ExportMarkdownAsync("export");
```

## Откуда берётся семантика

У PDF нет стандарта преобразования в Markdown. Inkwright соединяет две нормативные модели:

- логическую структуру Tagged PDF из ISO 32000-2 §§14.7–14.9;
- синтаксис CommonMark 0.31.2 либо GitHub Flavored Markdown.

`RoleMap` и PDF 2.0 `RoleMapNS` разрешаются транзитивно. Порядок смешанного массива `/K`
сохраняется между дочерними элементами, MCID и `OBJR`; иначе абзац, ссылка или картинка могли бы
поменяться местами. `ActualText` на structure element и непосредственно на marked-content sequence
заменяет видимые глифы посимвольно, без схлопывания пробелов и переносов, как предписывает PDF.
Одинаковые MCID в page content и Form XObject различаются по `/Stm`; атрибуты читаются и из `/A`,
и из `ClassMap`, причём прямое значение имеет приоритет. Теги трактуются как стандартные только в
PDF namespace; совпавшее имя из пользовательского namespace не превращается, например, в ссылку.
`Alt` становится альтернативным текстом фигуры.

Основные соответствия:

| Tagged PDF | Markdown |
| --- | --- |
| `H1`…`H6` | ATX-заголовки `#`…`######` |
| `P`, `Caption` | абзац |
| `L` / `LI` / `LBody` | упорядоченный или неупорядоченный список по `ListNumbering` |
| description list | `<dl>` с диагностикой HTML fallback |
| простая `Table` | GFM pipe table |
| таблица со spans, row headers, смешанной header row либо режим CommonMark | HTML `<table>` |
| `Link` + `OBJR` URI action | Markdown-ссылка после проверки схемы |
| растровая `Figure` | PNG sidecar и Markdown image |
| `BlockQuote`, `Code`, `Strong`, `Em` | соответствующая Markdown-конструкция |
| `Formula` с presentation MathML | линейный текст формулы |
| `Form` + Widget либо `PrintField` | доступное имя, значение или состояние поля |

Decimal/Ordered использует нативный Markdown marker, включая явный цифровой `Lbl`. Roman/Alpha и
произвольный `Lbl` сохраняются как текст внутри пункта с `StructureApproximated`: иначе Markdown
молча показал бы другую метку. `ContinuedList`/`ContinuedFrom` также диагностируются, поскольку у
Markdown нет идентичности списка между раздельными блоками.

Внешние URI принимаются только для `http`, `https`, `mailto`, `ftp` и относительных целей.
Исполняемые и неизвестные абсолютные схемы, например `javascript:`, не попадают в ссылку и
фиксируются как `UnsafeLinkSkipped`. Перед проверкой декодируются HTML character references, поэтому
`javascript&#x3a;` не обходит allowlist; относительный URI разрешается через Catalog `/URI /Base`.
Внутренние PDF destinations пока остаются обычным текстом:
страничный адрес PDF не даёт стабильного якоря в Markdown.

## Нетегированные PDF

По умолчанию `PreferTagged` использует дерево структуры, когда оно есть. Для нетегированного файла
экспортёр берёт видимый текст страниц, экранирует Markdown и добавляет `UntaggedFallback`. Он не
угадывает молча заголовки, таблицы и колонки по внешнему виду: у фиксированной страницы несколько
правдоподобных семантических разборов.

```csharp
var options = new MarkdownExportOptions
{
    StructureMode = MarkdownStructureMode.RequireTagged,
};
```

`RequireTagged` отклоняет файл без structure tree. `PlainText` намеренно игнорирует теги и всегда
использует текст страниц. Это удобно для сравнения, но также отмечается диагностикой.

## Настройки

```csharp
var options = new MarkdownExportOptions
{
    Flavor = MarkdownFlavor.GitHub,             // значение по умолчанию
    StructureMode = MarkdownStructureMode.PreferTagged,
    IncludeImages = true,
    IncludeHiddenLayers = false,
    MediaDirectoryName = "assets/pdf images",
    MaximumPages = 10_000,
    MaximumStructureElements = 1_000_000,
    MaximumOutputCharacters = 64 * 1024 * 1024,
    MaximumImages = 10_000,
    MaximumTotalImageBytes = 512L * 1024 * 1024,
};

MarkdownDocument markdown = pdf.ToMarkdown(options);
```

`MediaDirectoryName` — переносимый относительный путь. Абсолютные пути, `.`/`..`, пустые сегменты,
управляющие символы и имена устройств Windows отклоняются до рендеринга. В Markdown пробелы и
прочие специальные байты сегментов кодируются как URL; на диске сохраняются исходные имена
каталогов.

Пределы страниц, структуры и текста останавливают экспорт исключением до возврата частичного
`MarkdownDocument`. Предел sidecar-изображений мягкий: дальнейший растр не удерживается в памяти,
`Figure` сохраняет альтернативный текст, а диагностика получает `ImageSkipped`. Общий предел
декодированного PDF stream дополнительно задаёт `PdfFilterLimits.MaxDecodedBytes`.

Изображение связывается с `Figure` по `OBJR` либо по фактическому вызову XObject внутри того же
MCID, включая Form XObject. Декодируемые растры приводятся к PNG, одинаковые PNG-байты получают
один sidecar в порядке первого упоминания. Векторная фигура, JPX или повреждённое изображение не
растеризуется: используется `Alt` и появляется `ImageSkipped`. Выключенный слой учитывается как на
marked content/Form, так и на самом Image XObject. Повторная ссылка на один уже не декодировавшийся
ресурс не запускает декодер снова.

## Метрики корректности

`MarkdownDocument.Metrics` предназначен для corpus/CI-проверок. Это не субъективная оценка качества,
а воспроизводимые счётчики: страницы и ошибки страниц, структура, distinct `(page, stream, MCID)` и
число разрешившихся ссылок, sidecar-изображения, неудачные image resources, предупреждения и длина
результата.

```csharp
MarkdownExportMetrics metrics = markdown.Metrics;
Console.WriteLine($"MCID coverage: {metrics.MarkedContentCoverage:P1}");
Console.WriteLine($"Skipped pages: {metrics.SkippedPages}");
```

`MarkedContentCoverage == 1` означает, что каждая distinct MCR-ссылка дала текст; ноль ссылок также
считается полным покрытием. Меньшее значение не обязательно ошибка библиотеки: Figure, векторный
контент и намеренно пустая marked-content sequence законно не дают глифов, поэтому причину следует
сопоставить с `Diagnostics`.

## Детерминированность и запись

Одинаковая модель с одинаковыми настройками даёт одинаковые текст, порядок диагностик, порядок
изображений и имена `image1.png`, `image2.png`, … Текст использует LF и ровно один завершающий LF.
`SaveAsync` пишет UTF-8 без BOM, перезаписывает только принадлежащие текущему результату файлы и не
удаляет посторонние или устаревшие файлы. Существующий symlink/junction в пути `document.md`, media
или sidecar отклоняется: одна лексическая проверка `..` не защищает от ухода через reparse point.
Pre-cancelled `ExportMarkdownAsync` прекращается до рендера и создания каталога. Отмена после начала
записи не транзакционна: уже записанные файлы могут остаться на диске.

Повреждение decode/content одной страницы даёт `ContentSkipped` с номером страницы и не мешает
обработать следующие. U+0000 никогда не попадает в Markdown: он заменяется на U+FFFD.

Диагностика — часть контракта, а не журнал исключений. Она сообщает о нетегированном fallback,
структурном приближении, HTML fallback, пропущенном содержимом, небезопасной ссылке или картинке,
которую нельзя было надёжно сохранить. Одинаковые `Kind` и `Subject` выдаются один раз.

Экспортёр не применяет разрешение владельца `SourcePermissions.CopyContents` автоматически. Если
приложение обязано соблюдать ограничения исходного PDF, оно должно проверить `SourcePermissions`
до `ToMarkdown` или `ExportMarkdownAsync`.
