# Быстрый старт

Требуется .NET 10 SDK.

```bash
dotnet add package Inkwright            # ядро: чтение, запись, правка, извлечение текста
dotnet add package Inkwright.Layout     # компоновщик отчётов, теги, PDF/A
dotnet add package Inkwright.Forms      # интерактивные формы
dotnet add package Inkwright.Signing    # подписи
```

## Собрать документ

```csharp
using Inkwright;
using Inkwright.Content;

using var document = PdfDocument.Create();
PdfPage page = document.Pages.Add(PageSize.A4);

using (ContentCanvas canvas = ContentCanvas.ForPage(page))
{
    canvas.DrawText("Привет", 72, 700, document.Fonts.Helvetica, 24, PdfColor.Black);
    canvas.FillColor(PdfColor.Parse("#2278D4")).Rectangle(72, 600, 200, 60).Fill();
    canvas.Commit();
}

document.Info.Title = "Первый файл";
document.Save("hello.pdf");
```

## Отчёт, который сам растекается по страницам

```csharp
using Inkwright.Layout;

Report.Create()
    .Page(page =>
    {
        page.Size(PageSize.A4);
        page.Margin(45);
        page.Header().Text("Отчёт за квартал", TextStyle.Default.FontSize(18).Bold());
        page.Content().Column(column =>
        {
            column.Spacing(10);
            column.Item().Text("Выручка держится на уровне прошлого квартала.");
            column.Item().Table(table =>
            {
                table.Columns(ColumnWidth.Fraction(3), ColumnWidth.Fraction(1));
                table.Grid();
                table.Header().Text("Статья").Text("Сумма");
                table.Row().Text("Услуги").Text("29 620");
            });
        });
        page.Footer().AlignCenter().Text(text => text.PageNumbers("{0} из {1}"));
    })
    .Save("report.pdf");
```

## Прочитать чужой файл

```csharp
using PdfDocument document = PdfDocument.Load("input.pdf");

Console.WriteLine($"{document.Pages.Count} страниц, версия {document.Version}");

foreach (PdfWarning warning in document.Diagnostics)
    Console.WriteLine($"  {warning}");

string text = document.Pages[0].ExtractText();
```

Файл с испорченной таблицей перекрёстных ссылок откроется так же: читатель найдёт объекты
сканированием и запишет в `Diagnostics`, что именно пришлось чинить.

## Поправить и дописать

```csharp
using PdfDocument document = PdfDocument.Load("invoice.pdf");

using (ContentCanvas canvas = ContentCanvas.ForPage(document.Pages[0]))
{
    canvas.Save()
          .Opacity(0.15)
          .Transform(PdfMatrix.Rotation(38) * PdfMatrix.Translation(140, 240))
          .DrawText("ОПЛАЧЕНО", 0, 0, document.Fonts.HelveticaBold, 72, PdfColor.Parse("#1B7F3B"))
          .Restore();
    canvas.Commit();
}

document.SaveIncremental("invoice-paid.pdf");   // исходные байты не тронуты
```

## Прокомментировать

```csharp
using Inkwright.Annotations;

using PdfDocument document = PdfDocument.Load("contract.pdf");
PdfPage page = document.Pages[0];

page.Annotations.MarkText("сроки поставки", PdfAnnotationType.Highlight, author: "Прия");

PdfTextAnnotation note = page.Annotations.AddNote(
    new PdfPoint(505, 700), "Ставка не изменилась?", "Прия", PdfNoteIcon.Help);

note.Reply("Нет, прежняя", "Том");
note.SetReviewState(PdfReviewState.Completed, "Прия");

document.SaveIncremental("reviewed.pdf");
```

Каждый комментарий получает готовый appearance-поток, поэтому выглядит одинаково во всех
просмотрщиках. Удаление каскадное: `note.Delete()` уносит popup-окно и всю ветку ответов.

## Что дальше

- [Архитектура](architecture.md) — из чего библиотека сложена и почему
- [Чтение и правка](reading.md) — диагностика, восстановление, шифрование
- [Компоновка](layout.md) — Measure/Draw, таблицы, колонтитулы, разрывы
- [Текст и шрифты](text.md) — встраивание, сабсеттинг, извлечение с позициями
- [Комментарии и аннотации](annotations.md) — заметки, выделения, ветки ответов, статусы
- [Формы](forms.md) — заполнение, appearance, flatten, типизированный маппинг
- [Подписи](signing.md) — PAdES, метки времени, внешние ключи
- [Архивные документы](archival.md) — теги, PDF/A, Factur-X
- [Бенчмарки](benchmarks.md)
