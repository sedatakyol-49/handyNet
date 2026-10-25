namespace HandyNet.Modules.Notifications.Domain;

/// <summary>
/// Texts of all emails in the seven UI locales (de, en, tr, es, ru, ar, fa). Placeholders are written as {name} and are
/// identical in every locale. German is the platform default and the fallback for unknown languages. The brand "SEHA"
/// is never translated.
/// </summary>
public static class EmailText
{
    /// <summary>Supported locales; the order matches the order of the values in the text table.</summary>
    public static readonly IReadOnlyList<string> Locales = ["de", "en", "tr", "es", "ru", "ar", "fa"];

    public const string Fallback = "de";

    /// <summary>Primary subtag of <paramref name="language"/> when supported ("fa-IR" becomes "fa"), otherwise German.</summary>
    public static string Normalize(string? language)
    {
        var primary = language?.Trim().Split('-', '_')[0].ToLowerInvariant();
        return primary is not null && Locales.Contains(primary) ? primary : Fallback;
    }

    public static bool IsRtl(string language) => Normalize(language) is "ar" or "fa";

    public static string Get(string key, string language) => Table[key][IndexOf(language)];

    /// <summary>Text with every {name} replaced by its value; values are inserted verbatim.</summary>
    public static string Get(string key, string language, params (string Name, string Value)[] values)
    {
        var text = Get(key, language);
        foreach (var (name, value) in values)
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        return text;
    }

    public static IEnumerable<string> Keys => Table.Keys;

    private static int IndexOf(string language) => Locales.ToList().IndexOf(Normalize(language));

    private static string[] T(string de, string en, string tr, string es, string ru, string ar, string fa) => [de, en, tr, es, ru, ar, fa];

    /// <summary>Spoken length of a link's validity, e.g. "48 hours", inflected as the sentence around it needs.</summary>
    public static string Duration(TimeSpan validFor, string language)
    {
        language = Normalize(language);
        if (validFor.TotalHours >= 1 && validFor.TotalMinutes % 60 == 0)
        {
            var h = (int)validFor.TotalHours;
            return language switch
            {
                "en" => h == 1 ? "1 hour" : $"{h} hours",
                "tr" => $"{h} saat",
                "es" => h == 1 ? "1 hora" : $"{h} horas",
                "ru" => h % 10 == 1 && h % 100 != 11 ? $"{h} часа" : $"{h} часов",
                "ar" => h switch { 1 => "ساعة واحدة", 2 => "ساعتين", >= 3 and <= 10 => $"{h} ساعات", _ => $"{h} ساعة" },
                "fa" => $"{h} ساعت",
                _ => h == 1 ? "1 Stunde" : $"{h} Stunden",
            };
        }
        var m = (int)Math.Ceiling(validFor.TotalMinutes);
        return language switch
        {
            "en" => $"{m} minutes",
            "tr" => $"{m} dakika",
            "es" => m == 1 ? "1 minuto" : $"{m} minutos",
            "ru" => m % 10 == 1 && m % 100 != 11 ? $"{m} минуты" : $"{m} минут",
            "ar" => m switch { 1 => "دقيقة واحدة", 2 => "دقيقتين", >= 3 and <= 10 => $"{m} دقائق", _ => $"{m} دقيقة" },
            "fa" => $"{m} دقیقه",
            _ => $"{m} Minuten",
        };
    }

    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["greeting"] = T("Guten Tag,", "Hello,", "Merhaba,", "Hola,", "Здравствуйте,", "مرحباً،", "سلام،"),
        ["greeting.named"] = T("Guten Tag {name},", "Hello {name},", "Merhaba {name},", "Hola {name},", "Здравствуйте, {name}!", "مرحباً {name}،", "سلام {name}،"),
        ["signature"] = T("Ihr SEHA-Team", "Your SEHA team", "SEHA Ekibi", "El equipo de SEHA", "Команда SEHA", "فريق SEHA", "تیم SEHA"),

        ["order.placed.subject"] = T("Neue Bestellung von {buyer}", "New order from {buyer}", "{buyer} tarafından yeni sipariş",
            "Nuevo pedido de {buyer}", "Новый заказ от {buyer}", "طلب جديد من {buyer}", "سفارش جدید از {buyer}"),
        ["order.placed.body"] = T(
            "{buyer} hat bei {seller} eine Bestellung über {amount} (brutto) aufgegeben.\nDer Bestand ist reserviert. Bitte bestätigen oder lehnen Sie die Bestellung ab:",
            "{buyer} has placed an order with {seller} worth {amount} (gross).\nThe stock is reserved. Please confirm or reject the order:",
            "{buyer}, {seller} firmasına {amount} (brüt) tutarında bir sipariş verdi.\nStok ayrıldı. Lütfen siparişi onaylayın veya reddedin:",
            "{buyer} ha realizado un pedido a {seller} por un importe de {amount} (bruto).\nEl stock está reservado. Confirme o rechace el pedido:",
            "{buyer} оформил(а) у {seller} заказ на сумму {amount} (брутто).\nТовар зарезервирован. Пожалуйста, подтвердите или отклоните заказ:",
            "قدّم {buyer} طلباً لدى {seller} بقيمة {amount} (شامل الضريبة).\nتم حجز المخزون. يُرجى تأكيد الطلب أو رفضه:",
            "{buyer} از {seller} سفارشی به مبلغ {amount} (ناخالص) ثبت کرده است.\nموجودی رزرو شده است. لطفاً سفارش را تأیید یا رد کنید:"),

        ["order.status.Confirmed.subject"] = T("Ihre Bestellung wurde bestätigt", "Your order has been confirmed", "Siparişiniz onaylandı",
            "Su pedido ha sido confirmado", "Ваш заказ подтверждён", "تم تأكيد طلبك", "سفارش شما تأیید شد"),
        ["order.status.Confirmed.sentence"] = T("{other} hat Ihre Bestellung bestätigt.", "{other} has confirmed your order.", "{other} siparişinizi onayladı.",
            "{other} ha confirmado su pedido.", "{other} подтвердил(а) ваш заказ.", "قام {other} بتأكيد طلبك.", "{other} سفارش شما را تأیید کرد."),
        ["order.status.Shipped.subject"] = T("Ihre Bestellung wurde versendet", "Your order has been shipped", "Siparişiniz gönderildi",
            "Su pedido ha sido enviado", "Ваш заказ отправлен", "تم شحن طلبك", "سفارش شما ارسال شد"),
        ["order.status.Shipped.sentence"] = T("{other} hat Ihre Bestellung als versendet markiert.", "{other} has marked your order as shipped.",
            "{other} siparişinizi gönderildi olarak işaretledi.", "{other} ha marcado su pedido como enviado.",
            "{other} отметил(а) ваш заказ как отправленный.", "قام {other} بتعليم طلبك على أنه مشحون.", "{other} سفارش شما را به‌عنوان ارسال‌شده ثبت کرد."),
        ["order.status.Rejected.subject"] = T("Ihre Bestellung wurde abgelehnt", "Your order has been rejected", "Siparişiniz reddedildi",
            "Su pedido ha sido rechazado", "Ваш заказ отклонён", "تم رفض طلبك", "سفارش شما رد شد"),
        ["order.status.Rejected.sentence"] = T("{other} hat Ihre Bestellung abgelehnt.", "{other} has rejected your order.", "{other} siparişinizi reddetti.",
            "{other} ha rechazado su pedido.", "{other} отклонил(а) ваш заказ.", "قام {other} برفض طلبك.", "{other} سفارش شما را رد کرد."),
        ["order.status.Cancelled.subject"] = T("Bestellung storniert", "Order cancelled", "Sipariş iptal edildi", "Pedido cancelado",
            "Заказ отменён", "تم إلغاء الطلب", "سفارش لغو شد"),
        ["order.status.Cancelled.sentence"] = T(
            "{other} hat die Bestellung storniert. Der reservierte Bestand wurde freigegeben.",
            "{other} has cancelled the order. The reserved stock has been released.",
            "{other} siparişi iptal etti. Ayrılan stok serbest bırakıldı.",
            "{other} ha cancelado el pedido. El stock reservado se ha liberado.",
            "{other} отменил(а) заказ. Зарезервированный товар снова доступен.",
            "قام {other} بإلغاء الطلب. تم تحرير المخزون المحجوز.",
            "{other} سفارش را لغو کرد. موجودی رزروشده آزاد شد."),
        ["order.status.other.subject"] = T("Bestellstatus: {status}", "Order status: {status}", "Sipariş durumu: {status}", "Estado del pedido: {status}",
            "Статус заказа: {status}", "حالة الطلب: {status}", "وضعیت سفارش: {status}"),
        ["order.status.other.sentence"] = T("Der Status der Bestellung ist jetzt {status}.", "The order status is now {status}.", "Siparişin durumu artık {status}.",
            "El estado del pedido es ahora {status}.", "Статус заказа теперь: {status}.", "أصبحت حالة الطلب الآن {status}.", "وضعیت سفارش اکنون {status} است."),
        ["order.status.reason"] = T("Begründung: {reason}", "Reason: {reason}", "Gerekçe: {reason}", "Motivo: {reason}", "Причина: {reason}", "السبب: {reason}", "دلیل: {reason}"),
        ["order.status.details"] = T("Details: {link}", "Details: {link}", "Ayrıntılar: {link}", "Detalles: {link}", "Подробности: {link}", "التفاصيل: {link}", "جزئیات: {link}"),

        ["verify.subject"] = T("Bitte bestätigen Sie Ihre E-Mail-Adresse", "Please confirm your email address", "Lütfen e-posta adresinizi doğrulayın",
            "Confirme su dirección de correo electrónico", "Подтвердите адрес электронной почты", "يُرجى تأكيد عنوان بريدك الإلكتروني", "لطفاً نشانی ایمیل خود را تأیید کنید"),
        ["verify.intro"] = T(
            "bitte bestätigen Sie die E-Mail-Adresse Ihres SEHA-Kontos über diesen Link:",
            "please confirm your email address for your SEHA account by opening this link:",
            "SEHA hesabınızın e-posta adresini şu bağlantıyla doğrulayın:",
            "Confirme la dirección de correo electrónico de su cuenta de SEHA abriendo este enlace:",
            "Подтвердите адрес электронной почты вашей учётной записи SEHA, перейдя по этой ссылке:",
            "يُرجى تأكيد البريد الإلكتروني لحسابك في SEHA عبر فتح هذا الرابط:",
            "لطفاً نشانی ایمیل حساب SEHA خود را با باز کردن این پیوند تأیید کنید:"),
        ["verify.outro"] = T(
            "Der Link ist {validity} gültig und kann einmal verwendet werden.\nFalls Sie kein SEHA-Konto angelegt haben, können Sie diese E-Mail ignorieren.",
            "The link is valid for {validity} and can be used once.\nIf you did not create a SEHA account, you can ignore this email.",
            "Bağlantı {validity} geçerlidir ve yalnızca bir kez kullanılabilir.\nSEHA hesabı oluşturmadıysanız bu e-postayı yok sayabilirsiniz.",
            "El enlace es válido durante {validity} y solo puede usarse una vez.\nSi no ha creado una cuenta de SEHA, puede ignorar este correo.",
            "Ссылка действительна в течение {validity} и может быть использована только один раз.\nЕсли вы не создавали учётную запись SEHA, просто проигнорируйте это письмо.",
            "الرابط صالح لمدة {validity} ويمكن استخدامه مرة واحدة فقط.\nإذا لم تُنشئ حساباً في SEHA، يمكنك تجاهل هذه الرسالة.",
            "این پیوند به مدت {validity} معتبر است و فقط یک بار قابل استفاده است.\nاگر حسابی در SEHA ایجاد نکرده‌اید، می‌توانید این ایمیل را نادیده بگیرید."),

        ["reset.subject"] = T("SEHA-Passwort zurücksetzen", "Reset your SEHA password", "SEHA parolanızı sıfırlayın", "Restablezca su contraseña de SEHA",
            "Сброс пароля SEHA", "إعادة تعيين كلمة مرور SEHA", "بازنشانی گذرواژه SEHA"),
        ["reset.intro"] = T(
            "wir haben eine Anfrage erhalten, das Passwort Ihres SEHA-Kontos zurückzusetzen. Über diesen Link vergeben Sie ein neues Passwort:",
            "we received a request to reset the password of your SEHA account. To choose a new password, open this link:",
            "SEHA hesabınızın parolasını sıfırlama yönünde bir talep aldık. Yeni bir parola belirlemek için şu bağlantıyı açın:",
            "Hemos recibido una solicitud para restablecer la contraseña de su cuenta de SEHA. Para elegir una nueva contraseña, abra este enlace:",
            "Мы получили запрос на сброс пароля вашей учётной записи SEHA. Чтобы задать новый пароль, перейдите по ссылке:",
            "تلقّينا طلباً لإعادة تعيين كلمة مرور حسابك في SEHA. لاختيار كلمة مرور جديدة، افتح هذا الرابط:",
            "درخواستی برای بازنشانی گذرواژه حساب SEHA شما دریافت کردیم. برای تعیین گذرواژه جدید، این پیوند را باز کنید:"),
        ["reset.outro"] = T(
            "Der Link ist {validity} gültig und kann einmal verwendet werden. Mit dem neuen Passwort werden Sie auf allen Geräten abgemeldet.\nFalls Sie das nicht angefordert haben, können Sie diese E-Mail ignorieren – Ihr Passwort bleibt unverändert.",
            "The link is valid for {validity} and can be used once. Setting a new password signs you out on all devices.\nIf you did not request this, you can ignore this email — your password stays unchanged.",
            "Bağlantı {validity} geçerlidir ve yalnızca bir kez kullanılabilir. Yeni parola belirlediğinizde tüm cihazlardaki oturumlarınız kapatılır.\nBu talebi siz yapmadıysanız bu e-postayı yok sayabilirsiniz; parolanız değişmeden kalır.",
            "El enlace es válido durante {validity} y solo puede usarse una vez. Al establecer una nueva contraseña, se cerrará su sesión en todos los dispositivos.\nSi no lo ha solicitado usted, puede ignorar este correo: su contraseña no cambiará.",
            "Ссылка действительна в течение {validity} и может быть использована только один раз. После смены пароля вы будете выведены из аккаунта на всех устройствах.\nЕсли вы не запрашивали сброс, просто проигнорируйте это письмо — ваш пароль не изменится.",
            "الرابط صالح لمدة {validity} ويمكن استخدامه مرة واحدة فقط. عند تعيين كلمة مرور جديدة سيتم تسجيل خروجك من جميع الأجهزة.\nإذا لم تطلب ذلك، يمكنك تجاهل هذه الرسالة — ستبقى كلمة مرورك كما هي.",
            "این پیوند به مدت {validity} معتبر است و فقط یک بار قابل استفاده است. با تعیین گذرواژه جدید، از همه دستگاه‌ها خارج می‌شوید.\nاگر این درخواست از طرف شما نبوده، می‌توانید این ایمیل را نادیده بگیرید؛ گذرواژه شما تغییری نمی‌کند."),

        ["org.verified.subject"] = T("{org} ist freigeschaltet", "{org} has been activated", "{org} etkinleştirildi", "{org} ya está activada",
            "{org} активирована", "تم تفعيل {org}", "{org} فعال شد"),
        ["org.verified.body"] = T(
            "Ihr Unternehmen {org} wurde geprüft und auf SEHA freigeschaltet.\nDie für Sie aktivierten Module finden Sie nach der Anmeldung:",
            "Your company {org} has been reviewed and activated on SEHA.\nAfter signing in you will find the modules enabled for you:",
            "{org} şirketiniz incelendi ve SEHA'da etkinleştirildi.\nSizin için etkinleştirilen modülleri oturum açtıktan sonra bulabilirsiniz:",
            "Su empresa {org} ha sido revisada y activada en SEHA.\nTras iniciar sesión encontrará los módulos habilitados para usted:",
            "Ваша компания {org} проверена и активирована в SEHA.\nДоступные вам модули вы найдёте после входа в систему:",
            "تمت مراجعة شركتك {org} وتفعيلها على SEHA.\nستجد الوحدات المفعّلة لك بعد تسجيل الدخول:",
            "شرکت شما {org} بررسی و در SEHA فعال شد.\nماژول‌های فعال‌شده برای شما را پس از ورود خواهید دید:"),

        ["receipt.vatid"] = T("USt-IdNr.", "VAT ID", "KDV No.", "NIF/IVA", "Номер плательщика НДС", "الرقم الضريبي", "شناسه مالیاتی"),
        ["receipt.number"] = T("Nr.", "No.", "No.", "n.º", "№", "رقم", "شماره"),
        ["receipt.discount"] = T("Rabatt", "Discount", "İndirim", "Descuento", "Скидка", "خصم", "تخفیف"),
        ["receipt.total"] = T("Summe", "Total", "Toplam", "Total", "Итого", "المجموع", "جمع کل"),
        ["receipt.rounding"] = T("Rundung", "Rounding", "Yuvarlama", "Redondeo", "Округление", "التقريب", "گرد کردن"),
        ["receipt.net"] = T("Netto", "Net", "Net", "Neto", "Нетто", "الصافي", "خالص"),
        ["receipt.vat"] = T("USt", "VAT", "KDV", "IVA", "НДС", "ضريبة القيمة المضافة", "مالیات بر ارزش افزوده"),
        ["receipt.gross"] = T("Brutto", "Gross", "Brüt", "Bruto", "Брутто", "شامل الضريبة", "ناخالص"),
        ["receipt.change"] = T("Rückgeld", "Change", "Para üstü", "Cambio", "Сдача", "الباقي", "مبلغ برگشتی"),
        ["receipt.thanks"] = T("Vielen Dank für Ihren Einkauf!", "Thank you for your purchase!", "Alışverişiniz için teşekkür ederiz!", "¡Gracias por su compra!",
            "Спасибо за покупку!", "شكراً لتسوقكم معنا!", "از خرید شما سپاسگزاریم!"),
        ["receipt.cash"] = T("Bar", "Cash", "Nakit", "Efectivo", "Наличные", "نقداً", "نقدی"),
        ["receipt.card"] = T("Karte", "Card", "Kart", "Tarjeta", "Карта", "بطاقة", "کارت"),

        ["doc.quotation"] = T("anbei erhalten Sie unser Angebot {number} vom {date}.", "Please find attached our quotation {number} dated {date}.",
            "{date} tarihli {number} numaralı teklifimiz ekte yer almaktadır.", "Adjuntamos nuestra oferta {number} de fecha {date}.",
            "Направляем вам наше предложение {number} от {date}.", "نرفق لكم عرضنا {number} بتاريخ {date}.", "پیشنهاد ما به شماره {number} مورخ {date} پیوست است."),
        ["doc.document"] = T("anbei erhalten Sie die {title} {number} vom {date}.", "Please find attached {title} {number} dated {date}.",
            "{date} tarihli {title} {number} ekte yer almaktadır.", "Adjuntamos {title} {number} de fecha {date}.",
            "Направляем вам документ: {title} {number} от {date}.", "نرفق لكم {title} {number} بتاريخ {date}.", "{title} {number} مورخ {date} پیوست است."),
        ["doc.amount"] = T("Betrag: {amount}", "Amount: {amount}", "Tutar: {amount}", "Importe: {amount}", "Сумма: {amount}", "المبلغ: {amount}", "مبلغ: {amount}"),
        ["doc.validuntil"] = T("Gültig bis: {date}", "Valid until: {date}", "Geçerlilik tarihi: {date}", "Válido hasta: {date}", "Действительно до: {date}",
            "صالح حتى: {date}", "معتبر تا: {date}"),
        ["doc.dueby"] = T("Zahlbar bis: {date}", "Payable by: {date}", "Son ödeme tarihi: {date}", "Vencimiento: {date}", "Оплатить до: {date}",
            "تاريخ الاستحقاق: {date}", "سررسید پرداخت: {date}"),
        ["doc.attachment"] = T("Das Dokument finden Sie als PDF im Anhang.", "The document is attached as a PDF.", "Belgeyi PDF olarak ekte bulabilirsiniz.",
            "Encontrará el documento en PDF adjunto.", "Документ во вложении в формате PDF.", "ستجدون المستند مرفقاً بصيغة PDF.", "سند به‌صورت PDF پیوست شده است."),
        ["doc.closing"] = T("Mit freundlichen Grüßen", "Kind regards", "Saygılarımızla", "Un cordial saludo", "С уважением,", "مع خالص التحية", "با احترام"),
    };
}
