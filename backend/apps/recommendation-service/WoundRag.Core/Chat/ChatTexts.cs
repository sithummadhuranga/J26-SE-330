// Every sentence the chat shows that code, not a model, writes, in English, Sinhala and Tamil.
// REVIEW NEEDED: the Sinhala and Tamil texts were written for this demo and are not yet checked by a native speaker
// (demo decision 47), nor is the topic notice added on 19 Oct 2026; its one-passage form repeats the plural until they
// are. Field names and removal kinds stay in English inside them, as the protocol uses those terms.

namespace WoundRag.Core.Chat;

// The fixed texts.
public static class ChatTexts
{
    private static readonly ChatTextSet English = new(
        NotCovered: "The guideline passages available for this assessment do not answer this question.",
        OutOfScope: "This assistant answers questions about the IWGDF guideline evidence for this assessment only. It cannot diagnose or advise beyond the guideline.",
        Blocked: "No answer passed the safety checks, so none is shown. The protocol above still applies.",
        Unavailable: "The answer service cannot be reached just now. Please try again in a moment.",
        TranslationUnverified: "The translation could not be checked, so the answer is shown in English.",
        WithheldTemplate: "Some guidance stays withheld until these are recorded in a new assessment: {0}.",
        RemovedTemplate: "Removed before sending: {0}.",
        WithheldTopicTemplate: "{0} guideline passages on this topic are withheld until these are recorded in a new assessment: {1}.",
        WithheldTopicOneTemplate: "{0} guideline passage on this topic is withheld until these are recorded in a new assessment: {1}.");

    private static readonly ChatTextSet Sinhala = new(
        NotCovered: "මෙම තක්සේරුව සඳහා ඇති මාර්ගෝපදේශ කොටස් මෙම ප්‍රශ්නයට පිළිතුරු නොදෙයි.",
        OutOfScope: "මෙම සහායකය පිළිතුරු දෙන්නේ මෙම තක්සේරුවට අදාළ IWGDF මාර්ගෝපදේශ සාක්ෂි පිළිබඳ ප්‍රශ්නවලට පමණි. රෝග විනිශ්චය කිරීමට හෝ මාර්ගෝපදේශයෙන් ඔබ්බට උපදෙස් දීමට එයට නොහැක.",
        Blocked: "ආරක්ෂක පරීක්ෂාවන් සමත් වූ පිළිතුරක් නොමැති නිසා කිසිවක් පෙන්වා නැත. ඉහත ප්‍රොටෝකෝලය තවමත් අදාළ වේ.",
        Unavailable: "පිළිතුරු සේවාවට මේ මොහොතේ සම්බන්ධ විය නොහැක. කරුණාකර මොහොතකින් නැවත උත්සාහ කරන්න.",
        TranslationUnverified: "පරිවර්තනය පරීක්ෂා කළ නොහැකි වූ බැවින් පිළිතුර ඉංග්‍රීසියෙන් පෙන්වා ඇත.",
        WithheldTemplate: "නව තක්සේරුවක මේවා සටහන් කරන තුරු සමහර මඟපෙන්වීම් රඳවා තබා ඇත: {0}.",
        RemovedTemplate: "යැවීමට පෙර ඉවත් කළ දේ: {0}.",
        WithheldTopicTemplate: "මෙම මාතෘකාව පිළිබඳ මාර්ගෝපදේශ කොටස් {0}ක් නව තක්සේරුවක මේවා සටහන් කරන තුරු රඳවා තබා ඇත: {1}.",
        WithheldTopicOneTemplate: "මෙම මාතෘකාව පිළිබඳ මාර්ගෝපදේශ කොටස් {0}ක් නව තක්සේරුවක මේවා සටහන් කරන තුරු රඳවා තබා ඇත: {1}.");

    private static readonly ChatTextSet Tamil = new(
        NotCovered: "இந்த மதிப்பீட்டுக்குக் கிடைக்கும் வழிகாட்டுதல் பகுதிகள் இந்தக் கேள்விக்குப் பதிலளிக்கவில்லை.",
        OutOfScope: "இந்த உதவியாளர் இந்த மதிப்பீட்டுக்கான IWGDF வழிகாட்டுதல் சான்றுகள் பற்றிய கேள்விகளுக்கு மட்டுமே பதிலளிக்கும். இது நோயறிதல் செய்யவோ வழிகாட்டுதலுக்கு அப்பால் ஆலோசனை வழங்கவோ முடியாது.",
        Blocked: "பாதுகாப்புச் சரிபார்ப்புகளில் தேறிய பதில் இல்லாததால் எதுவும் காட்டப்படவில்லை. மேலே உள்ள நெறிமுறை தொடர்ந்து பொருந்தும்.",
        Unavailable: "பதில் சேவையை இப்போது அணுக முடியவில்லை. சிறிது நேரத்தில் மீண்டும் முயற்சிக்கவும்.",
        TranslationUnverified: "மொழிபெயர்ப்பைச் சரிபார்க்க முடியாததால் பதில் ஆங்கிலத்தில் காட்டப்படுகிறது.",
        WithheldTemplate: "புதிய மதிப்பீட்டில் இவை பதிவு செய்யப்படும் வரை சில வழிகாட்டுதல்கள் நிறுத்தி வைக்கப்பட்டுள்ளன: {0}.",
        RemovedTemplate: "அனுப்பும் முன் நீக்கப்பட்டவை: {0}.",
        WithheldTopicTemplate: "இந்தத் தலைப்பில் {0} வழிகாட்டுதல் பகுதிகள் புதிய மதிப்பீட்டில் இவை பதிவு செய்யப்படும் வரை நிறுத்தி வைக்கப்பட்டுள்ளன: {1}.",
        WithheldTopicOneTemplate: "இந்தத் தலைப்பில் {0} வழிகாட்டுதல் பகுதிகள் புதிய மதிப்பீட்டில் இவை பதிவு செய்யப்படும் வரை நிறுத்தி வைக்கப்பட்டுள்ளன: {1}.");

    public static ChatTextSet For(string language) => language switch
    {
        ChatLanguage.English => English,
        ChatLanguage.Sinhala => Sinhala,
        ChatLanguage.Tamil => Tamil,
        _ => throw new ArgumentException($"unknown language '{language}'", nameof(language)),
    };
}
