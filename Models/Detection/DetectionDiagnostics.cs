// Подключаем Stopwatch — он даёт монотонное время с высоким разрешением,
// в отличие от DateTime, который может «прыгнуть» при переводе часов
using System.Diagnostics;

// Объявляем пространство имён для моделей данных распознавания
namespace MauiBluethooth.Models.Detection;

// Замер времени одного этапа конвейера: сколько миллисекунд занял конкретный шаг.
// Собирается из замеров, которые делает ObjectDetector по ходу обработки кадра
public readonly record struct TimingStage(string Name, double Milliseconds)
{
	// Пустой этап — используется как «заглушка», пока ни один этап не измерен
	public static TimingStage Empty { get; } = new("—", 0);
}

// Снимок диагностики: что и насколько быстро делает детектор прямо сейчас.
//
// Зачем это отдельный тип. Распознавание — тяжёлая операция, и на реальном телефоне
// тормоза видны только в цифрах. Страница «Диагностика» показывает эти числа, и по ним
// видно, что именно тормозит: копирование кадра, преобразование цвета, поворот,
// сам нейросетевой проход или разбор выходов
public sealed record DetectionDiagnostics
{
	// Конструктор снимка диагностики
	public DetectionDiagnostics(
		// Идентификатор модели, для которой сняты замеры
		string modelId,
		// Список этапов с их длительностью
		IReadOnlyList<TimingStage> stages,
		// Число объектов в последнем кадре
		int objectCount,
		// Момент, когда снимок был заполнен
		long timestampMilliseconds)
	{
		// Сохраняем идентификатор модели
		ModelId = modelId;

		// Сохраняем этапы
		Stages = stages;

		// Сохраняем число объектов
		ObjectCount = objectCount;

		// Сохраняем метку времени
		TimestampMilliseconds = timestampMilliseconds;
	}

	// Счётчик, из которого берётся текущее время процесса.
	// Вынесен отдельно, чтобы замеры не зависели от переводов системных часов
	internal static readonly Stopwatch Clock = Stopwatch.StartNew();

	// Идентификатор активной модели
	public string ModelId { get; }

	// Этапы конвейера и их длительность
	public IReadOnlyList<TimingStage> Stages { get; }

	// Сколько объектов найдено в последнем обработанном кадре
	public int ObjectCount { get; }

	// Момент заполнения снимка
	public long TimestampMilliseconds { get; }

	// Суммарное время обработки одного кадра, мс
	public double TotalMilliseconds => Stages.Sum(stage => stage.Milliseconds);

	// Плавно усреднённый FPS. Считается как 1000 / среднее время кадра
	public double Fps => TotalMilliseconds > 0 ? 1000.0 / TotalMilliseconds : 0;

	// Итоговое время кадра одной строкой — для компактной подписи в интерфейсе
	public string TotalText => $"{TotalMilliseconds:F2} мс";
}
