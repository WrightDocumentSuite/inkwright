# Формы

## Чтение и заполнение

```csharp
using Inkwright.Forms;

using PdfDocument document = PdfDocument.Load("application.pdf");
PdfForm? form = PdfForm.Open(document);

foreach (PdfFormField field in form!)
    Console.WriteLine($"{field.Type} {field.FullName} = {field.Value}");

form.Set("applicant.name", "Ада Лавлейс");
form.Set("applicant.remote", true);
form["applicant.country"]!.SetValue("GB");

document.Save("application-filled.pdf");
```

Поля адресуются полным именем — так, как о них думает тот, кто заполняет форму: дерево полей
обходится один раз, части имени соединяются точками.

`PdfFormField` знает тип (`Text`, `CheckBox`, `RadioButton`, `PushButton`, `ComboBox`, `ListBox`,
`Signature`), флаги, подсказку, ограничение длины, список вариантов, состояния переключателя и
прямоугольник на странице.

## Appearance-потоки

Спецификация разрешает читателю строить внешний вид поля самому из строки `/DA`, и многие читатели
этого не делают. Поэтому библиотека строит его сама: после каждой записи значения текстовое поле,
список и выпадающий список получают свежий XObject с текстом, обрезанным по рамке и выровненным по
`/Q`. Переключатели уже несут вид для каждого состояния — меняется только выбранное.

Из-за этого заполненная форма выглядит заполненной везде, а не только в одном просмотрщике.

## Форматирование как в Acrobat

Acrobat хранит формат поля не как данные, а как вызов одного из своих встроенных скриптов в
действии `/AA /F`. Документ, показывающий итог как `$1,234.50`, на самом деле несёт строку
`AFNumber_Format(2, 0, 0, 0, "$", true)`.

Библиотека читает эти вызовы, не запуская никакого JavaScript, и запекает результат в
appearance-поток. Поэтому поле выглядит правильно и в просмотрщике без скриптового движка.

```csharp
PdfFormField total = form["invoice.total"]!;

total.SetValue("1234.5");
Console.WriteLine(total.Value);          // 1234.5   — что записано
Console.WriteLine(total.DisplayValue);   // $1,234.50 — что видно
```

Разбираются `AFNumber_Format`, `AFPercent_Format`, `AFDate_Format`, `AFDate_FormatEx`,
`AFTime_Format`, `AFTime_FormatEx` и `AFSpecial_Format` (индекс, индекс+4, телефон, номер
социального страхования). Стили разделителей, стили отрицательных чисел и позиция знака валюты —
все как в таблице Acrobat.

## Вычисляемые поля

Так же хранится и арифметика: `AFSimple_Calculate("SUM", ["net", "tax"])` в действии `/AA /C`.

```csharp
form.Set("net", "1200");
form.Set("tax", "34.5");
form.Recalculate();                       // вернёт, сколько полей изменилось

Console.WriteLine(form["total"]!.Value);  // 1234.5
```

Порядок берётся из массива `/CO` формы, и это важно: общий итог, складывающий подытоги, верен
только если подытоги посчитаны раньше. Поля, которых массив не назвал, считаются после всех
названных.

Поддерживаются SUM, AVG, PRD, MIN и MAX. Числа читаются снисходительно: значение, уже несущее
разделители, символ валюты или хвост текста, всё равно даёт число внутри себя.

## Обмен данными: XFDF и FDF

Заполненную форму отправляют обратно не файлом целиком, а её значениями. Форматов два — XFDF
(ISO 19444-1 §6.3) и более старый FDF (ISO 32000-1 §12.7.8.3.2, синтаксис самого PDF); оба возят и
значения, и комментарии.

```csharp
// Отправить значения
string xfdf = document.ExportFormData(href: "application.pdf");   // XFDF
byte[] fdf = document.ExportFormDataFdf();                         // FDF

// Принять их в чистый бланк — формат распознаётся сам
PdfReviewImportResult fromXfdf = blank.ImportXfdf(xfdf);
PdfReviewImportResult fromFdf = blank.ImportFdf(fdf);
Console.WriteLine($"{fromXfdf.FieldsFilled} полей заполнено");

// Запечь значения в внешний вид, чтобы их рисовал любой просмотрщик
PdfForm.Open(blank)!.RegenerateAppearances();
```

`ExportXfdf` и `ExportFdf` по умолчанию пишут обе половины — значения и комментарии;
`PdfExchangeContent.Fields` оставляет только первую, `ExportFormData`/`ExportFormDataFdf` — то же
короче. Обе стороны обмена — чтение и запись — теперь симметричны в обоих форматах; раньше FDF возил
только комментарии.

### Иерархия имён

В PDF полное имя поля пишется через точку, в XFDF та же иерархия выражается вложенностью. Поле
`Address.Name` едет так:

```xml
<fields>
  <field name="Address">
    <field name="Name"><value>Adobe Systems, Inc.</value></field>
  </field>
</fields>
```

Точка — разделитель, поэтому §12.7.3.2 запрещает точку внутри частичного имени. `AddTextField` с
именем `Address.Name` строит ветку `Address` и вешает поле на неё, а не записывает точку в `/T`.
Обращаться к полю по-прежнему можно по полному имени.

### Чего обмен не может

Импорт меняет только значения полей, которые в документе уже есть (§5.6.1). Имя, которого документ
не несёт, попадает в `Warnings`, а не создаётся: ни XFDF, ни FDF нечем сказать, какого типа было бы
такое поле и где бы оно лежало.

Подписное поле значения не экспортирует (§6.3.3.1, §12.7.8.3.2) — то, что оно хранит, вне
подписанного файла ничего не значит.

Список с множественным выбором едет несколькими элементами `<value>`, перенос строки в многострочном
поле — одним переводом строки, флажок — именем своего включённого состояния.

### Внешний вид после импорта

Импорт пишет значения прямо в словари и выставляет `NeedAppearances`, потому что нарисованное
больше не соответствует записанному. Просмотрщик со своим генератором перерисует поля сам;
`RegenerateAppearances()` делает это заранее, и тогда форма выглядит заполненной везде.

## Создание полей

```csharp
PdfForm form = PdfForm.OpenOrCreate(document);

form.AddTextField(page, PdfRectangle.FromSize(200, 700, 300, 22), "applicant.name");
form.AddTextField(page, PdfRectangle.FromSize(200, 640, 300, 60), "applicant.address", multiline: true);
form.AddCheckBox(page, PdfRectangle.FromSize(200, 600, 16, 16), "applicant.remote");
form.AddComboBox(page, PdfRectangle.FromSize(200, 560, 120, 22), "applicant.currency", ["EUR", "GBP", "USD"]);
```

## Уплощение

```csharp
form.Flatten();
```

Внешний вид каждого поля впечатывается в содержимое страницы, аннотации удаляются, `AcroForm`
исчезает из каталога. Отображение сохраняется до пикселя, потому что рисуется тот же XObject,
который показывал просмотрщик; отменить это уже нельзя — в том и смысл.

## Типизированный маппинг

```csharp
[PdfForm]
public partial record Invoice(
    [property: PdfField("customer.name")] string Customer,
    [property: PdfField("invoice.number")] int Number,
    [property: PdfField("invoice.total", Format = "0.00")] decimal Total,
    [property: PdfField("invoice.paid")] bool Paid,
    [property: PdfField("invoice.date")] DateOnly Date);
```

Source generator реализует `IPdfFormSchema<Invoice>` с `static abstract` членами:

```csharp
form.Fill(new Invoice("Ада Лавлейс", 2026014, 1234.50m, true, DateOnly.FromDateTime(DateTime.Today)));

Invoice read = form.Read<Invoice>();
IReadOnlyList<string> missing = form.MissingFields<Invoice>();
```

Ни одного обращения к рефлексии: имена полей проверены компилятором, код работает под Native AOT и
переживает обрезку. Поддерживаются строки, целые, дробные, `decimal`, `bool`, перечисления,
`DateTime`, `DateOnly`, `DateTimeOffset` и `Guid`; тип, который не отображается на значение поля,
даёт предупреждение `IWF002`, а непартиальный тип — ошибку `IWF001`. Тип, вложенный в другой,
поддерживается, если все объемлющие типы объявлены `partial`.
