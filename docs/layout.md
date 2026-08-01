# Компоновка

`Inkwright.Layout` описывает документ как содержимое, а не как страницы. Движок раскладывает его
дважды: первый проход считает страницы, второй рисует — поэтому колонтитул может сказать
«страница 3 из 12», хотя в момент описания никто этого числа не знал.

## Measure и Draw

Каждый элемент отвечает на два вопроса. `Measure` сообщает, что он сделал бы с предложенным местом,
и ничего не меняет. `Draw` делает это.

```csharp
public abstract SpacePlan Measure(LayoutSize available, LayoutContext context);
public abstract void Draw(LayoutSize available, LayoutContext context);
```

`SpacePlan` — не размер, а решение: `Empty`, `Wrap` (сюда не влезает вовсе), `Partial` (влезла
часть, остальное на следующую страницу) или `Full`. Именно `Partial` и позволяет содержимому течь
через страницы без второй модели раскладки.

Элемент, нарисовавший часть себя, помнит место; `Reset` возвращает его в начало. Считающий проход
идёт без канвы: состояние продвигается, а рисования не происходит.

## Система координат

Layout думает сверху вниз, PDF — снизу вверх. Переворот делается один раз в матрице страницы, после
чего `(0, 0)` — левый верхний угол текущего элемента, а `y` растёт вниз. Текст и картинки
переворачиваются обратно внутри `LayoutCanvas`; всё остальное — прямоугольники, линии, клипы —
работает в y-вниз естественно.

## Fluent-описание

Каждое украшение возвращает слот внутри себя, поэтому цепочка читается как вложенность, которую
создаёт:

```csharp
page.Content()
    .Padding(20)
    .Background("#F5F5F5", cornerRadius: 4)
    .Padding(12)
    .Text("В рамке с отступами");
```

Контейнеры:

```csharp
container.Column(column => { column.Spacing(8); column.Item().Text("…"); });
container.Row(row => { row.ConstantItem(120).Text("слева"); row.RelativeItem(2).Text("шире"); });
container.Table(table => { … });
container.Image(image, ImageFit.Contain);
container.Canvas(60, (canvas, size) => canvas.FillRectangle(0, 0, size.Width, size.Height, PdfColor.Red));
container.PageBreak();
container.Line(0.5, PdfColor.Gray(0.8f));
container.Spacer(12);
```

Размещение и размеры: `AlignLeft`, `AlignCenter`, `AlignRight`, `AlignTop`, `AlignMiddle`,
`AlignBottom`, `Width`, `Height`, `MinHeight`, `MaxWidth`, `ShowIf`.

## Текст

```csharp
container.Text(text =>
{
    text.Style(TextStyle.Default.FontSize(11).FontFamily("Segoe UI"));
    text.Justify();
    text.Span("Обычный текст, ");
    text.Span("выделенный", TextStyle.Default.Bold().FontColor("#B3261E"));
    text.Span(", и снова обычный.");
    text.Line(string.Empty);
    text.PageNumbers("Страница {0} из {1}");
});
```

Строки ломаются жадно по точкам, где перенос разрешён: пробелы, дефисы, косая черта и границы
глифов CJK. Слово, которое не влезает целиком, режется посимвольно. Выключка растягивает промежутки
между словами и никогда сами слова; последняя строка абзаца остаётся рваной.

Стиль — immutable record, поэтому наследование бесплатно, а одинаковые стили сравниваются по
значению. На строку выводится один текстовый объект, а идущие подряд фрагменты одного стиля
склеиваются в один оператор показа: страница с абзацем из ста слов содержит несколько операторов,
а не шестьсот.

Шрифт разрешается по семейству: сначала встроенные четырнадцать, потом установленный в системе
(`SystemFonts`), потом Helvetica. Явный `TextStyle.Font(font)` перекрывает всё.

## Таблицы

```csharp
container.Table(table =>
{
    table.Columns(ColumnWidth.Fraction(4), ColumnWidth.Fixed(60), ColumnWidth.Fraction(1));
    table.Grid(0.4, PdfColor.Gray(0.8f));
    table.CellPadding(6);

    table.Header()
        .Background(PdfColor.Parse("#EFF3F8"))
        .Text("Описание", TextStyle.Default.Bold())
        .Text("Кол-во")
        .Text("Сумма");

    foreach (var line in lines)
        table.Row().Text(line.Name).Text(line.Quantity).Text(line.Total);
});
```

Заголовочные строки повторяются вверху каждой страницы, которую таблица занимает. Их состояние
перед каждой страницей сбрасывается — иначе повтор вышел бы пустым, потому что текст в ячейке
помнит, сколько уже нарисовал.

## Разрывы страниц

`container.PageBreak()` заканчивает страницу. Разрыв, оказавшийся вверху страницы, уже сделал своё
дело и уступает место вместо того, чтобы оставить за собой пустой лист.

## Когда не влезает

Элемент, которому не хватит места ни на какой странице, вызывает `LayoutException` с деревом
элементов в `ElementTrace`:

```
Nothing fitted on page 2, so the layout would never finish.

Element tree:
Container
  ColumnElement
    Container
      TextElement
    Container
      ConstrainedElement
```

Имя в трассе берётся из `DebugName`, если оно задано, иначе из типа элемента.

## Своё рисование

Там, где модели не хватает, можно взять канву:

```csharp
container.Canvas(80, (canvas, size) =>
{
    canvas.FillRectangle(0, 0, size.Width, size.Height, PdfColor.Parse("#EEF2FF"));
    canvas.DrawLine(0, size.Height / 2, size.Width, size.Height / 2, 1, PdfColor.Blue);
});
```

`LayoutCanvas` даёт прямоугольники, скруглённые прямоугольники, линии, обводки, текст, картинки и
клипы в координатах layout; `canvas.Content` — полный низкоуровневый `ContentCanvas`.
