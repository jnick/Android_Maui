// Объявляем пространство имён для моделей данных распознавания
namespace MauiBluethooth.Models.Detection;

// Описание одной доступной модели: по нему строится список выбора в интерфейсе.
// Модели перечисляются кодом (см. ModelRegistry), а не читаются из файловой системы:
// список получается предсказуемым, и опечатку в имени файла невозможно пропустить мимо компилятора
public sealed record ModelDescriptor
{
	// Конструктор описания модели
	public ModelDescriptor(
		// Короткий идентификатор, который хранится в настройках
		string id,
		// Понятное человеку название для выпадающего списка
		string displayName,
		// Категория модели
		DetectionModelKind kind,
		// Размер входа сети в пикселях (для классических моделей не используется)
		int inputSize,
		// Ссылка на папку с моделью, если она нужна
		string assetName)
	{
		// Сохраняем идентификатор
		Id = id;

		// Сохраняем отображаемое название
		DisplayName = displayName;

		// Сохраняем категорию
		Kind = kind;

		// Сохраняем размер входа
		InputSize = inputSize;

		// Сохраняем имя ресурса с моделью
		AssetName = assetName;
	}

	// Идентификатор модели для сохранения выбора
	public string Id { get; }

	// Название для интерфейса
	public string DisplayName { get; }

	// Категория модели
	public DetectionModelKind Kind { get; }

	// Размер входа сети в пикселях. 0 — у классических моделей, у них нет свёрточной сети
	public int InputSize { get; }

	// Имя файла модели в Resources/Raw/models
	public string AssetName { get; }
}
