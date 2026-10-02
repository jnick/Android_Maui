// Подключаем геометрию MAUI — рамки результатов хранятся в RectF
using Microsoft.Maui.Graphics;

// Объявляем пространство имён для математических помощников детектора
namespace MauiBluethooth.Services.Detection.Helpers;

// Подавление неуверенных дубликатов (Non-Maximum Suppression, NMS).
//
// Зачем это нужно. Сеть не выдаёт готовые рамки — она выдаёт МНОГО кандидатов,
// и на одном лице их может быть пять-десять штук, почти друг на друге.
// Если нарисовать их все, картинка превратится в кашу из прямоугольников.
// NMS решает это так: рамки сортируются по уверенности, и любая рамка,
// перекрывающая уже выбранную сильнее порога, отбрасывается
public static class NonMaxSuppression
{
	// Считает, насколько две рамки перекрываются: IoU = площадь пересечения / площадь объединения.
	// Возвращает число от 0 (не пересекаются) до 1 (совпадают)
	public static float IntersectionOverUnion(RectF a, RectF b)
	{
		// Пересечение: берём максимум левых и верхних краёв и минимум правых и нижних.
		// Если результат получился отрицательным, рамки не пересекаются
		var left = Math.Max(a.Left, b.Left);
		var top = Math.Max(a.Top, b.Top);
		var right = Math.Min(a.Right, b.Right);
		var bottom = Math.Min(a.Bottom, b.Bottom);

		// Ширина и высота пересечения. Отрицательные — значит пересечения нет
		var overlapWidth = right - left;
		var overlapHeight = bottom - top;

		// Проверяем на пересечение до умножения: если одна из сторон не положительна,
		// площадь пересечения равна нулю
		if (overlapWidth <= 0f || overlapHeight <= 0f)
		{
			// Рамки не пересекаются
			return 0f;
		}

		// Площадь пересечения
		var intersection = overlapWidth * overlapHeight;

		// Площадь объединения двух рамок. Площади обеих рамок уже посчитаны, повторно не считаем
		var union = (a.Width * a.Height) + (b.Width * b.Height) - intersection;

		// Вырожденный случай: рамка нулевой площади не должна приводить к делению на ноль
		if (union <= 0f)
		{
			// Считаем рамки полностью совпадающими, чтобы NMS убрал дубликат
			return 1f;
		}

		// Возвращаем отношение площадей
		return intersection / union;
	}

	// Подавляет дубликаты в одном классе: все рамки считаются экземплярами одного объекта
	public static List<int> Suppress(
		IReadOnlyList<RectF> boxes,
		IReadOnlyList<float> scores,
		float iouThreshold,
		float scoreThreshold,
		int maxKeep = 300)
		=> Suppress(boxes, scores, classIds: null, iouThreshold, scoreThreshold, maxKeep);

	// Подавляет дубликаты с учётом классов: рамка «человек» не подавляет рамку «велосипед»,
	// даже если они полностью совпали. Это стандартное поведение детекторов COCO
	public static List<int> Suppress(
		IReadOnlyList<RectF> boxes,
		IReadOnlyList<float> scores,
		IReadOnlyList<int>? classIds,
		float iouThreshold,
		float scoreThreshold,
		int maxKeep = 300)
	{
		// Пустой вход — пустой выход
		if (boxes.Count == 0)
		{
			// Возвращаем новый пустой список, чтобы вызывающий код не получил null
			return [];
		}

		// Отбор по уверенности. Сортируем по убыванию: сортировка индексирующих перестановок
		// недоступна в .NET, поэтому сортируем массив пар «индекс — уверенность»
		var order = new int[boxes.Count];
		for (var i = 0; i < boxes.Count; i++)
		{
			// Изначально каждый индекс соответствует себе
			order[i] = i;
		}

		// Сортируем перестановку по убыванию уверенности.
		// Сортировка нестабильная, но это неважно: рамки с одинаковой уверенностью всё равно
		// выбираются произвольно, а на результат влияет лишь сам факт выбора
		Array.Sort(order, (left, right) => scores[right].CompareTo(scores[left]));

		// Результат: индексы рамок, которые выжили
		var kept = new List<int>(Math.Min(maxKeep, boxes.Count));

		// Подавлять будем только среди рамок одного класса, поэтому храним выбранные
		// рамки и их классы рядом
		var keptClasses = new List<int>(maxKeep);

		// Проходим кандидатов от самой уверенного к самой неуверенной
		foreach (var index in order)
		{
			// Слишком неуверенные кандидаты заканчиваются раньше остальных:
			// список отсортирован, поэтому можно просто прекратить проход
			if (scores[index] < scoreThreshold)
			{
				// Всё, что дальше, ещё менее уверенно — выходим
				break;
			}

			// Класс текущего кандидата. Если классы не переданы, считаем все рамки одним классом
			var currentClass = classIds is null ? 0 : classIds[index];

			// Проверяем, не подавляется ли кандидат уже выбранной рамкой
			var suppressed = false;

			// Перебираем только рамки того же класса — чужие классы не влияют
			for (var keptPosition = 0; keptPosition < kept.Count; keptPosition++)
			{
				// Пропускаем рамки других классов
				if (keptClasses[keptPosition] != currentClass)
				{
					// Классы не совпадают — сравнивать нечего
					continue;
				}

				// Считаем перекрытие с конкретной выбранной рамкой
				var iou = IntersectionOverUnion(boxes[index], boxes[kept[keptPosition]]);

				// Если рамки перекрываются сильнее порога — это дубль, выкидываем
				if (iou > iouThreshold)
				{
					// Помечаем кандидата как подавленного и прерываем проверку
					suppressed = true;
					break;
				}
			}

			// Подавленного кандидата пропускаем
			if (suppressed)
			{
				// К следующему кандидату
				continue;
			}

			// Дошли до конца списка выбранных — дальше ограничивать нечего
			if (kept.Count >= maxKeep)
			{
				// Прекращаем проход
				break;
			}

			// Рамка не дубль — принимаем её в результат
			kept.Add(index);

			// Запоминаем класс принятой рамки, чтобы следующие кандидаты сравнивались с ней
			keptClasses.Add(currentClass);
		}

		// Возвращаем индексы выбранных рамок
		return kept;
	}
}
