// Подключаем математические структуры OpenCV
using OpenCvSharp;

// Подключаем модуль DNN: в OpenCvSharp 4.x сетевые функции вынесены из Cv2 в CvDnn
using OpenCvSharp.Dnn;

// Подключаем геометрию интерфейса для DetectedObject
using Microsoft.Maui.Graphics;

// Подключаем наши модели данных
using MauiBluethooth.Models.Detection;

// Подключаем препроцессинг кадра, подавление дубликатов и названия классов COCO
using MauiBluethooth.Services.Detection.Helpers;
using MauiBluethooth.Services.Detection.Preprocessing;

// Размер для OpenCV пишем явно: в проекте есть ещё Size из Microsoft.Maui.Graphics,
// и без псевдонима компилятор не может выбрать нужный
using CvSize = OpenCvSharp.Size;

// Объявляем пространство имён для конкретных моделей распознавания
namespace MauiBluethooth.Services.Detection.Models;

// Детектор объектов YOLOX (версия 2022 года) поверх OpenCV DNN.
//
// В отличие от NanoDet, эталонный yolox.py из зоопарка можно использовать:
// формат модели не менялся. Вход — 1×3×640×640 в порядке RGB со значениями 0..255
// без нормировки, выход — один тензор 1×8400×85. Именно его разбирает этот класс
//
// Что означает 8400. Это количество ячеек трёхмасштабной сетки:
// 80×80 + 40×40 + 20×20 = 6400 + 1600 + 400 = 8400.
// Что означает 85. На каждую ячейку сеть выдаёт 5 чисел рамки
// (центр по X и Y, ширина, высота, уверенность в объекте) и 80 уверенностей классов.
//
// Главное отличие от NanoDet: у YOLOX якоря ЗАРАНЕЕ ЗАДАНЫ и совпадают с центрами
// ячеек сетки. Сеть не предсказывает расстояние от якоря, а выдаёт смещение
// относительно него и размер. Поэтому рамка собирается сложением, а не вычитанием
public sealed class YoloXModel : IDetectionModel
{
	// Размер входа сети
	private const int InputSize = 640;

	// Шаги пирамиды: три масштаба.
	// Порядок важен: якоря масштабов в выходе идут именно в таком порядке,
	// и перестановка сдвинула бы каждую рамку
	private static readonly int[] Strides = [8, 16, 32];

	// Сколько чисел занимает одна ячейка: 4 параметра рамки, 1 уверенность
	// в объекте и 80 уверенностей классов
	private const int ValuesPerAnchor = 5 + CocoClasses.Count;

	// Индексы первых пяти чисел ячейки — рамка и уверенность в объекте
	private const int OffsetCenterX = 0;
	private const int OffsetCenterY = 1;
	private const int OffsetWidth = 2;
	private const int OffsetHeight = 3;
	private const int OffsetObjectness = 4;

	// Порог подавления дубликатов. Это значение из исходной статьи YOLOX
	// и из стандартных настроек COCO-детекторов
	private const float NmsThreshold = 0.45f;

	// Порог, ниже которого включается упрощённое подавление.
	//
	// Что за упрощение. При очень низком пороге (0,001, как в стандартных настройках
	// COCO) в выходе лежат десятки тысяч кандидатов. Их уверенности настолько
	// низкие, что ни один из них не может перекрыть другой сильнее порога подавления,
	// и проверки можно не делать вовсе. Это стандартный приём из YOLOv5/YOLOX,
	// он ускоряет разбор в разы. При пороге, который выставляет человек в интерфейсе,
	// упрощение не включается — там кандидатов мало и точность важнее
	private const float FastNmsScoreThreshold = 0.001f;

	// Загруженная сеть
	private readonly Net _net;

	// Замеры последнего вызова Detect
	private readonly List<TimingStage> _stages = [];

	// Рамки-кандидаты текущего кадра
	private readonly List<DetectedObject> _candidates = [];

	// Номера классов кандидатов — нужны для подавления по классам
	private readonly List<int> _candidateClasses = [];

	// Конструктор модели: читает сеть
	public YoloXModel(ModelDescriptor descriptor, string modelPath)
	{
		// Сохраняем описание модели
		Descriptor = descriptor;

		// Проверяем наличие файла: иначе сообщение OpenCV не будет содержать имени файла
		if (!File.Exists(modelPath))
		{
			// Явно называем путь
			throw new FileNotFoundException($"Не найден файл модели YOLOX: {modelPath}", modelPath);
		}

		// Загружаем сеть
		_net = CvDnn.ReadNetFromOnnx(modelPath)
			?? throw new InvalidOperationException("OpenCV вернул пустую сеть YOLOX.");

		// Считаем на CPU: DNN-бэкенды OpenCV в NuGet-пакет не входят,
		// доступен только встроенный
		_net.SetPreferableTarget(Target.CPU);
	}

	// Описание модели
	public ModelDescriptor Descriptor { get; }

	// Замеры последнего распознавания
	public IReadOnlyList<TimingStage> LastStages => _stages;

	// Распознаёт объекты в кадре
	public void Detect(Mat bgrFrame, float scoreThreshold, List<DetectedObject> results)
	{
		// Наполняем результат заново
		results.Clear();

		// Обнуляем замеры прошлого кадра
		_stages.Clear();

		// Момент старта подготовки входа
		var preprocessStart = DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds;

		// Вписываем кадр в квадрат 640×640 серыми полями.
		// Значение полей 114 — из эталонного примера YOLOX, и это важно:
		// сеть видела при обучении именно серые полосы, другой цвет снижает точность
		using var box = Letterbox.Apply(bgrFrame, InputSize);

		// Собираем вход сети.
		// Здесь единственное место во всём проекте, где swapRB = true:
		// YOLOX обучен на RGB, а вписывание даёт нам BGR. BlobFromImage при
		// swapRB = true переставляет каналы, то есть превращает BGR в RGB.
		// Нормировки нет вообще: сеть ждёт значения 0..255 как есть
		using var blob = CvDnn.BlobFromImage(
			box.Letterboxed, 1.0, new CvSize(InputSize, InputSize), default, swapRB: true, crop: false);

		// Записываем время подготовительного этапа
		_stages.Add(new TimingStage("Подготовка входа", DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds - preprocessStart));

		// Момент старта сетевого прохода
		var forwardStart = DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds;

		// Забираем выход. У YOLOX он ОДИН, поэтому здесь как раз подходит
		// перегрузка Forward без имени — она возвращает единственный Mat.
		// (У NanoDet выходов шесть, и там приходится сначала узнавать имена слоёв)
		using var output = _net.Forward();

		// Замеряем чистое время сети
		_stages.Add(new TimingStage("YOLOX: сеть", DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds - forwardStart));

		// Момент старта разбора
		var decodeStart = DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds;

		// Проверяем форму выхода. Сеть должна вернуть [1, 8400, 85].
		// Сверяем не по одному размеру, а по двум сразу: так отсекается и чужая
		// модель, и наша же, но скачанная в другой версии
		var anchors = output.Size(1);
		var features = output.Size(2);
		if (features != ValuesPerAnchor)
		{
			// Показываем, что получили: сразу видно, что модель не та
			throw new InvalidOperationException(
				$"Выход YOLOX имеет последнее измерение {features}, а ожидалось {ValuesPerAnchor}. " +
				"Скорее всего, скачана другая версия модели.");
		}

		// Очищаем рабочие буферы
		_candidates.Clear();
		_candidateClasses.Clear();

		// Забираем все 714 тысяч чисел одним куском без копирования
		var data = DnnOutput.AsFloats(output, anchors * ValuesPerAnchor, "YOLOX_output");

		// Сколько ячеек занимает один масштаб: при шаге 8 это 80×80 = 6400 ячеек
		var cellsPerLevel = (InputSize / Strides[0]) * (InputSize / Strides[0]);

		// Начало текущего масштаба в общем списке ячеек
		var levelStart = 0;

		// Разбираем масштабы
		for (var level = 0; level < Strides.Length; level++)
		{
			// Число ячеек у этого масштаба
			var cells = (InputSize / Strides[level]) * (InputSize / Strides[level]);

			// Число ячеек в строке: 80, 40 или 20
			var grid = InputSize / Strides[level];

			// Шаг сетки этого масштаба
			var stride = Strides[level];

			// Проверяем, что сетка действительно начинается там, где мы ожидали.
			// Это дешёвая страховка от молчаливых неверных рамок, если порядок
			// масштабов в модели вдруг окажется другим
			if (levelStart != level * cellsPerLevel)
			{
				// Сообщаем оба числа: расхождение сразу видно
				throw new InvalidOperationException(
					$"Масштаб {Strides[level]} YOLOX начинается с ячейки {levelStart}, " +
					$"а ожидалась {level * cellsPerLevel}. Порядок якорей в модели другой.");
			}

			// Разбираем каждую ячейку этого масштаба
			for (var cell = 0; cell < cells; cell++)
			{
				// Начало строки этой ячейки в плоском массиве
				var row = (levelStart + cell) * ValuesPerAnchor;

				// Уверенность в объекте: сколько сеть уверена, что здесь вообще
				// что-то есть, независимо от класса
				var objectness = data[row + OffsetObjectness];

				// Дешёвый отсев: если даже уверенность в объекте ниже порога,
				// то и итоговая уверенность будет ниже, потому что уверенность
				// класса никогда не превышает единицу, а итог — это произведение.
				// Проверка одного числа против перебора 80 классов экономит
				// почти всё время разбора: у 8400 ячеек большинство пустых
				if (objectness < scoreThreshold)
				{
					// Ячейка пустая — переходим к следующей
					continue;
				}

				// Ищем самый уверенный класс среди 80
				var bestClass = 0;
				var bestScore = 0f;

				// Начало блока классов этой ячейки: он идёт сразу после пяти
				// чисел рамки, то есть со смещения 5
				var classStart = row + 5;

				// Перебираем все 80 классов.
				// Считаем вручную, без LINQ: ячеек 8400, классов 80, это
				// 672 тысячи сравнений на кадр
				for (var c = 0; c < CocoClasses.Count; c++)
				{
					// Уверенность класса, умноженная на уверенность в объекте.
					// Именно произведение, а не уверенность класса: сеть разделила
					// задачу на две части, и для сравнения с порогом нужна обе
					var score = data[classStart + c] * objectness;

					// Если она больше лучшей найденной — запоминаем
					if (score > bestScore)
					{
						// Запоминаем номер класса
						bestClass = c;

						// И его уверенность
						bestScore = score;
					}
				}

				// Отсекаем неуверенные ячейки
				if (bestScore < scoreThreshold)
				{
					// Ячейка пустая — следующая
					continue;
				}

				// Координаты якоря внутри уровня
				var gx = cell % grid;
				var gy = cell / grid;

				// Смещение относительно якоря, предсказанное сетью.
				// Экспоненту применяем к размеру, а не к смещению
				var offsetX = data[row + OffsetCenterX];
				var offsetY = data[row + OffsetCenterY];
				var width = MathF.Exp(data[row + OffsetWidth]) * stride;
				var height = MathF.Exp(data[row + OffsetHeight]) * stride;

				// Собираем центр: якорь плюс предсказанное смещение.
				// Формула именно такая — умножение на шаг, а не сложение
				var centerX = (gx + offsetX) * stride;
				var centerY = (gy + offsetY) * stride;

				// Переходим от центра с размерами к левому верхнему углу
				var x = centerX - (width * 0.5f);
				var y = centerY - (height * 0.5f);

				// Зажимаем рамку в пределы входа: у края картинки сеть любит
				// предсказывать рамки, вылезающие за границу
				var left = Math.Clamp(x, 0f, InputSize);
				var top = Math.Clamp(y, 0f, InputSize);
				var right = Math.Clamp(x + width, 0f, InputSize);
				var bottom = Math.Clamp(y + height, 0f, InputSize);

				// Вырожденные рамки пропускаем
				if (right - left < 1f || bottom - top < 1f)
				{
					// Слишком маленькая рамка — не рисуем
					continue;
				}

				// Добавляем кандидата с подписью класса
				_candidates.Add(new DetectedObject(
					bounds: new RectF(left, top, right - left, bottom - top),
					label: CocoClasses.GetName(bestClass),
					confidence: bestScore));

				// Запоминаем класс для подавления по классам
				_candidateClasses.Add(bestClass);
			}

			// Сдвигаем начало на число ячеек текущего масштаба
			levelStart += cells;
		}

		// Подавляем дубликаты.
		// При очень низком пороге кандидатов тысячи, но все они настолько
		// неуверенные, что не могут перекрыть друг друга сильнее порога.
		// В этом случае подавление можно пропустить целиком — так и делает
		// оригинальная реализация YOLOX
		var skipNms = scoreThreshold < FastNmsScoreThreshold;

		// Список индексов рамок, которые останутся на экране
		List<int> kept;

		if (skipNms)
		{
			// Упрощённый путь: оставляем все рамки как есть.
			// Явно перечисляем индексы, чтобы дальше был один общий код
			kept = new List<int>(_candidates.Count);
			for (var i = 0; i < _candidates.Count; i++)
			{
				// Добавляем индекс в список оставшихся
				kept.Add(i);
			}
		}
		else
		{
			// Обычный путь: подавляем дубликаты по классам.
			// Список классов обязателен: рамка «человек» не должна подавлять
			// рамку «велосипед», даже если они полностью совпали
			kept = NonMaxSuppression.Suppress(
				ExtractBounds(_candidates),
				ExtractScores(_candidates),
				_candidateClasses,
				NmsThreshold,
				scoreThreshold);
		}

		// Возвращаем оставшиеся рамки в координаты кадра.
		// Порядок «сначала подавление, потом обратное преобразование» тот же,
		// что в остальных моделях: так пересчёт выполняется для десятка рамок,
		// а не для тысяч отброшенных
		foreach (var index in kept)
		{
			// Берём рамку во вписывании
			var candidate = _candidates[index];

			// Возвращаем левый верхний угол в кадр
			var topLeft = box.Unproject(candidate.Bounds.X, candidate.Bounds.Y);

			// Возвращаем правый нижний угол в кадр
			var bottomRight = box.Unproject(candidate.Bounds.Right, candidate.Bounds.Bottom);

			// Собираем готовый объект для интерфейса
			results.Add(new DetectedObject(
				bounds: new RectF(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y),
				label: candidate.Label,
				confidence: candidate.Confidence));
		}

		// Замеряем разбор выходов
		_stages.Add(new TimingStage("YOLOX: разбор", DetectionDiagnostics.Clock.Elapsed.TotalMilliseconds - decodeStart));
	}

	// Копирует рамки кандидатов в массив для подавления дубликатов
	private static RectF[] ExtractBounds(List<DetectedObject> items)
	{
		// Заводим массив нужного размера
		var result = new RectF[items.Count];

		// Копируем значения
		for (var i = 0; i < items.Count; i++)
		{
			// Заполняем очередной элемент
			result[i] = items[i].Bounds;
		}

		// Отдаём готовый массив
		return result;
	}

	// Копирует уверенности кандидатов в массив для подавления дубликатов
	private static float[] ExtractScores(List<DetectedObject> items)
	{
		// Заводим массив нужного размера
		var result = new float[items.Count];

		// Копируем значения
		for (var i = 0; i < items.Count; i++)
		{
			// Заполняем очередной элемент
			result[i] = items[i].Confidence;
		}

		// Отдаём готовый массив
		return result;
	}

	// Освобождаем нативную память сети
	public void Dispose() => _net.Dispose();
}
