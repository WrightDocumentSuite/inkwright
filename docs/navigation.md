# Навигация: закладки, назначения, output intents

## Закладки

Дерево закладок (§12.3.3) читается и правится через модель документа. `document.Outline` возвращает
`null`, если закладок нет; `PdfOutline.OpenOrCreate` заводит пустое дерево, к которому можно
добавлять.

```csharp
using PdfDocument document = PdfDocument.Load("book.pdf");

PdfOutline outline = PdfOutline.OpenOrCreate(document);
PdfOutlineItem chapter = outline.Add("Глава 1", PdfDestination.FitPage(document.Pages[0]));
chapter.Add("Раздел 1.1", PdfDestination.XyzLocation(document.Pages[1], top: 800));
chapter.SetColor(PdfColor.Rgb(0.1f, 0.1f, 0.6f));
chapter.SetStyle(italic: false, bold: true);

document.SaveIncremental("book-bookmarked.pdf");
```

Двусвязный список `/First`–`/Last`–`/Next`–`/Prev` и знаковый счётчик `/Count` (положительный —
раскрыт, отрицательный — свёрнут) пересобираются от корня при каждом изменении, как бы глубоко в
дереве оно ни было: раздел, добавленный на третьем уровне, поправит итоги всех предков над ним.

Удаление уносит с собой поддерево и чинит цепочку соседей:

```csharp
outline.Remove(chapter);          // вместе со всеми разделами под ним
```

## Назначения

`PdfDestination` — независимое от документа значение: одно и то же назначение можно отдать закладке,
ссылке или действию при открытии. Оно превращается в массив только при записи, и страница получает
объектный номер тогда же, если модель ещё его не присвоила.

| Фабрика | Что делает |
| --- | --- |
| `FitPage(page)` | вписать всю страницу (`/Fit`) |
| `XyzLocation(page, left, top, zoom)` | точное положение и масштаб (`/XYZ`); `null` — оставить как есть |
| `FitWidth(page, top)` | по ширине, заданная строка вверху (`/FitH`) |
| `FitHeight(page, left)` | по высоте, заданный столбец слева (`/FitV`) |

### Structure destinations (PDF 2.0)

Назначение может указывать не на страницу, а на элемент структуры (§12.3.2.3): читатель сам находит
страницу по первому содержимому элемента, поэтому ссылка переживает переверстку, сдвинувшую текст.
Первый элемент массива — косвенная ссылка на элемент структуры, а не на страницу.

```csharp
PdfStructureNode heading = document.Structure[0];      // прочитан как косвенный объект
item.SetStructureDestination(PdfDestination.ToStructureElement(heading, top: 750));
```

`SetStructureDestination` пишет обычную запись `/Dest`, но первым элементом её массива служит ссылка
на structure element вместо страницы. Документ при этом продвигается до PDF 2.0. Ключ `/SD`
применим только внутри действия GoTo и не является ключом словаря закладки. Важно для PDF/UA-2.

## Закладки из компоновщика

`Inkwright.Layout` умеет строить дерево закладок из содержимого: декоратор `Bookmark` не меняет
раскладку, но, когда содержимое ложится на страницу, запоминает, куда именно, и после отрисовки это
превращается в `/Outlines`. Заголовок и его закладка не разъедутся, как бы ни легли страницы.

```csharp
Report.Create()
    .Page(page => page.Content().Column(column =>
    {
        column.Item().Bookmark("Глава 1").Text("Глава 1", TextStyle.Default.FontSize(18).Bold());
        column.Item().Bookmark("Раздел 1.1", level: 1).Text("Раздел 1.1");
        column.Item().Bookmark("Глава 2").Text("Глава 2", TextStyle.Default.FontSize(18).Bold());
    }))
    .Save("report.pdf");
```

`level` задаёт вложенность: закладка, объявившая уровень глубже, чем сейчас доходит дерево, цепляется
к самой глубокой имеющейся записи.

В теговой секции (`page.Tagged()`) закладка получает не страницу, а **structure destination**: она
указывает на элемент структуры, который создало её содержимое, а если содержимое ничего не создало —
на объемлющий узел. Этого требует ISO 14289-2 §8.8 от любого назначения внутри документа, и без
этого отчёт не прошёл бы собственный валидатор PDF/UA-2. Нетеговая секция структуры не имеет, и её
закладки остаются назначениями на страницу.

## Output intents

Характеристику устройства, под которое готовили документ (§14.11.5), — встроенный ICC-профиль — можно
добавить и на весь документ, и, как позволяет PDF 2.0, на отдельную страницу: это нужно файлу,
собранному из страниц под разные устройства.

```csharp
document.AddOutputIntent(PdfOutputIntent.SRgb());
document.Pages[0].AddOutputIntent(PdfOutputIntent.FromIccProfile(cmykProfile, components: 4, "GTS_PDFX", "Press"));
```

Добавление page-level output intent продвигает версию документа до PDF 2.0.

`PdfOutputIntent.SRgb()` берёт профиль, который библиотека генерирует сама; `FromIccProfile` встраивает
любой другой.
