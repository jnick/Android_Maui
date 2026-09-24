// Объявляем пространство имён, где хранятся модели данных приложения
namespace MauiBluethooth.Models;

// Модель одного сообщения в чате с Bluetooth-устройством
public class ChatMessage
{
	// Текст сообщения
	public string Text { get; set; } = string.Empty;

	// Входящее сообщение (true — пришло от устройства, false — отправлено нами)
	public bool IsIncoming { get; set; }

	// Системное сообщение (статусы, уведомления) — не является частью переписки
	public bool IsSystem { get; set; }

	// Для XAML-привязки: показывать серое системное сообщение (перекрывает пузыри)
	public bool ShowSystem => IsSystem;

	// Для XAML-привязки: показывать пузырь входящего сообщения (слева)
	public bool ShowIncoming => !IsSystem && IsIncoming;

	// Для XAML-привязки: показывать пузырь исходящего сообщения (справа)
	public bool ShowOutgoing => !IsSystem && !IsIncoming;
}