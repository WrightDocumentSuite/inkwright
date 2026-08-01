# Сборка NuGet-пакетов

Решение выпускает пять пакетов: `Inkwright`, `Inkwright.Cjk`, `Inkwright.Forms`,
`Inkwright.Layout` и `Inkwright.Signing`. Проекты тестов, примеры, бенчмарки и отдельная сборка
генератора форм не пакуются. Генератор входит в `Inkwright.Forms` как analyzer asset.

Общие метаданные находятся в `Directory.Build.props`: версия, авторы, лицензия Apache-2.0, README,
адрес проекта и репозитория. Описание и поисковые теги каждого пакета находятся в его `.csproj`,
потому что назначение надстроек различается. NuGet показывает элемент `Description` из nuspec в
карточке пакета, а `README.md` — как подробное описание под ней.

## Собрать

Из корня `Inkwright`:

```bash
dotnet restore Inkwright.slnx
dotnet test Inkwright.slnx -c Release --no-restore
dotnet pack Inkwright.slnx -c Release --no-restore \
  -p:Version=0.1.0 \
  --output artifacts/packages
```

Параметр `Version` намеренно задаётся в команде выпуска: так один и тот же коммит можно собирать
как prerelease (`0.2.0-preview.1`) или стабильную версию без правки проекта. Если параметр не задан,
используется значение из `Directory.Build.props`.

Результат — по одному `.nupkg` на публикуемый проект в `artifacts/packages`. Перед публикацией
нужно открыть пакеты как ZIP и проверить `.nuspec`, `README.md`, `lib/net10.0`, зависимости и,
для `Inkwright.Forms`, `analyzers/dotnet/cs/Inkwright.Forms.Generator.dll`.

## Проверить установку локально

Новый пустой проект позволяет проверить не только nuspec, но и реальное разрешение зависимостей.
Создавайте его **вне дерева репозитория**: иначе central package management проекта перехватит
`dotnet add package` и команда попытается изменить рабочий `Directory.Packages.props`.

### Сначала выбросить старую версию из кэша

Это не предосторожность, а обязательный шаг, и без него проверка бессмысленна. Версия NuGet
неизменяема **по идентичности**: раз `Inkwright/0.1.0` оказался в `~/.nuget/packages`, restore берёт
оттуда и в локальный feed уже не смотрит. Пересобранный пакет с тем же номером остаётся лежать
нетронутым, а вы проверяете вчерашние байты и ничего об этом не узнаёте.

```bash
for id in inkwright inkwright.cjk inkwright.forms inkwright.layout inkwright.signing; do
  rm -rf ~/.nuget/packages/$id
done
```

Удаляются только эти пять записей: `dotnet nuget locals global-packages --clear` вычистит кэш
целиком и заставит все прочие проекты на машине качать зависимости заново.

Убедиться, что теперь взята свежая сборка, можно по времени файла:

```bash
ls -l ~/.nuget/packages/inkwright/0.1.0/lib/net10.0/Inkwright.dll
```

Оно должно совпадать со временем `dotnet pack`, а не быть раньше.

```bash
cd /path/outside/PDFLib
dotnet new console -n PackageSmoke -f net10.0
dotnet new nugetconfig -o PackageSmoke --force
dotnet nuget add source "/absolute/path/to/Inkwright/artifacts/packages" \
  --name inkwright-local \
  --configfile PackageSmoke/nuget.config
cd PackageSmoke
dotnet add package Inkwright.Signing --version 0.1.0
dotnet build -c Release --no-restore
```

Путь к источнику задавайте в виде Windows (`E:\...`): `dotnet nuget add source` отвергает путь с
прямыми слэшами как невалидный.

Созданный конфиг сохраняет nuget.org для внешних зависимостей и добавляет локальный источник.
Для полной проверки добавьте все пять package ID. Временный `PackageSmoke` в репозиторий не входит.

### Проверять поведение, а не факт компиляции

Пустой `Main`, который собрался, доказывает только то, что nuspec не сломан. Полезнее короткая
программа, которая прогоняет через собранные бинарники то, что менялось в этом выпуске: она заодно
проверяет **публичную поверхность** API. Внутренние типы вроде `PdfNames` тесты видят через
`InternalsVisibleTo`, а потребитель пакета — нет, и код, случайно опёршийся на такой тип в примерах
документации, компилируется у вас и не компилируется у пользователя.

## Опубликовать

После проверки конкретной версии:

```bash
dotnet nuget push "artifacts/packages/*.nupkg" \
  --api-key "$NUGET_API_KEY" \
  --source https://api.nuget.org/v3/index.json \
  --skip-duplicate
```

Версия NuGet неизменяема: повторная сборка с тем же номером не заменит опубликованный пакет.
Секрет API нельзя хранить в репозитории или передавать как MSBuild-свойство, которое попадёт в лог.
