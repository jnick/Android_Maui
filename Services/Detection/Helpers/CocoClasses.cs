// Объявляем пространство имён для математических помощников детектора
namespace MauiBluethooth.Services.Detection.Helpers;

// Названия классов набора COCO — тех же восьмидесяти, на которых обучены NanoDet и YOLOX.
//
// Откуда берутся номера классов. В модели они зашиты числами от 0 до 79,
// и расшифровать их может только этот список. Ошибка на единицу здесь
// выглядит забавно — «кит» вместо «человека», — поэтому список пишется вручную
// и сверяется с выводом эталонного примера. Проверено: на кадре толпы
// с уверенностью выше 0,3 модель выдаёт исключительно класс 0, то есть «человек»
public static class CocoClasses
{
	// Все восемьдесят названий в порядке номеров классов
	public static readonly string[] Names =
	[
		// 0–9: люди и то, что с ними связано
		"person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
		// 10–19: уличная обстановка
		"fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
		// 20–29: домашняя живность и еда
		"elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
		// 30–39: то, что человек держит в руках
		"skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket", "bottle",
		// 40–49: мелкие предметы вокруг
		"wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple", "sandwich", "orange",
		// 50–59: еда и кухня
		"broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch", "potted plant", "bed",
		// 60–69: мебель и техника
		"dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone", "microwave", "oven",
		// 70–79: оставшееся
		"toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush"
	];

	// Всего классов в наборе COCO
	public const int Count = 80;

	// Возвращает название класса по номеру.
	// Вне диапазона возвращаем заглушку, а не бросаем исключение: номер из сети
	// мы не контролируем, и из-за мусора в выходе приложение падать не должно
	public static string GetName(int classId)
		=> classId >= 0 && classId < Count ? Names[classId] : $"class {classId}";
}
