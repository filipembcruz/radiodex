using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace Radiodex.Services;

/// <summary>
/// All UI strings, English and Portuguese. XAML binds to <c>Loc.I[key]</c>;
/// switching language raises a change for every key so the UI updates live.
/// To add a language, add a column here.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    private static readonly Dictionary<string, (string En, string Pt)> Strings = new()
    {
        ["AppTagline"] = ("Internet radio for Windows", "Rádios da internet no Windows"),
        ["SearchOrPaste"] = ("Search stations or paste a link", "Buscar rádio ou colar link"),
        ["AddStation"] = ("Add station", "Adicionar rádio"),
        ["All"] = ("All", "Todas"),
        ["Favorites"] = ("Favorites", "Favoritas"),
        ["EmptyTitle"] = ("No stations yet", "Nenhuma rádio ainda"),
        ["EmptyHint"] = ("Search by name, city or genre, or paste a link from radios.com.br, Radio Garden or a direct stream.", "Busque por nome, cidade ou estilo, ou cole um link do radios.com.br, Radio Garden ou do stream direto."),
        ["Live"] = ("LIVE", "AO VIVO"),
        ["Paused"] = ("PAUSED", "PAUSADO"),
        ["Connecting"] = ("CONNECTING", "CONECTANDO"),
        ["NowPlaying"] = ("Now playing", "Tocando agora"),
        ["NoSongInfo"] = ("This station doesn't send song info", "Esta rádio não envia o nome das músicas"),
        ["Play"] = ("Play", "Tocar"),
        ["Pause"] = ("Pause", "Pausar"),
        ["Record"] = ("Record", "Gravar"),
        ["Recording"] = ("Recording", "Gravando"),
        ["Favorite"] = ("Favorite", "Favoritar"),
        ["Unfavorite"] = ("Remove from favorites", "Remover dos favoritos"),
        ["RefreshLink"] = ("Refresh link", "Atualizar link"),
        ["Refreshing"] = ("Refreshing…", "Atualizando…"),
        ["LikeSong"] = ("Like song", "Favoritar música"),
        ["SongLiked"] = ("Song liked", "Música favoritada"),
        ["StreamLink"] = ("Stream link", "Link do stream"),
        ["SourceLabel"] = ("Source", "Fonte"),
        ["Recordings"] = ("Recordings", "Gravações"),
        ["OpenFolder"] = ("Open folder", "Abrir pasta"),
        ["RecordingsHint"] = ("Saved in the stream's original format, with a .cue list of the songs.", "Salvas no formato original do stream, com uma lista .cue das músicas."),
        ["RecentlyPlayed"] = ("Recently played", "Tocou recentemente"),
        ["Volume"] = ("Volume", "Volume"),
        ["Remove"] = ("Remove station", "Remover rádio"),
        ["RemoveConfirm"] = ("Remove \"{0}\" from your list?", "Remover \"{0}\" da sua lista?"),
        ["StatusOnline"] = ("Online", "Online"),
        ["StatusChecked"] = ("Working · checked {0}", "Funcionando · verificado {0}"),
        ["StatusSearching"] = ("Looking for a new link…", "Buscando um novo link…"),
        ["StatusUpdated"] = ("Link updated automatically", "Link atualizado automaticamente"),
        ["StatusSame"] = ("Link checked, nothing changed", "Link verificado, nada mudou"),
        ["StatusFailed"] = ("No working link found. Retrying in 5 minutes.", "Nenhum link funcionando. Nova tentativa em 5 minutos."),
        ["StatusOffline"] = ("Offline", "Fora do ar"),
        ["NeverChecked"] = ("not checked yet", "ainda não verificado"),
        ["Today"] = ("today", "hoje"),
        ["DaysAgo"] = ("{0} days ago", "há {0} dias"),
        ["Yesterday"] = ("yesterday", "ontem"),
        ["RecordNotSupported"] = ("This stream type (HLS) can't be recorded yet.", "Este tipo de stream (HLS) ainda não pode ser gravado."),
        ["RecordSaved"] = ("Recording saved: {0}", "Gravação salva: {0}"),
        ["RecordFailed"] = ("Recording stopped: {0}", "Gravação interrompida: {0}"),
        ["PlayFailed"] = ("Couldn't play this station.", "Não foi possível tocar esta rádio."),
        // Add dialog
        ["AddTitle"] = ("Add station", "Adicionar rádio"),
        ["TabSearch"] = ("Search the world", "Buscar no mundo"),
        ["TabLink"] = ("Paste link", "Colar link"),
        ["SearchPlaceholder"] = ("Name, city, country or genre", "Nome, cidade, país ou estilo"),
        ["Search"] = ("Search", "Buscar"),
        ["LinkPlaceholder"] = ("radios.com.br, Radio Garden or direct stream link", "Link do radios.com.br, Radio Garden ou do stream"),
        ["Check"] = ("Check link", "Verificar link"),
        ["Add"] = ("Add", "Adicionar"),
        ["Added"] = ("Added", "Adicionada"),
        ["Searching"] = ("Searching…", "Buscando…"),
        ["Resolving"] = ("Opening the page and looking for the stream…", "Abrindo a página e procurando o stream…"),
        ["ResultsCount"] = ("{0} stations found · Radio Browser catalog", "{0} rádios encontradas · catálogo Radio Browser"),
        ["NoResults"] = ("No stations found. Try another name or city.", "Nenhuma rádio encontrada. Tente outro nome ou cidade."),
        ["Recognized"] = ("Recognized: {0}", "Reconhecido: {0}"),
        ["NotFoundInLink"] = ("Couldn't find a stream in this link.", "Não encontrei um stream neste link."),
        ["NetworkError"] = ("Network error: {0}", "Erro de rede: {0}"),
        ["SaveAndPlay"] = ("Save and play", "Salvar e tocar"),
        ["Cancel"] = ("Cancel", "Cancelar"),
        ["Close"] = ("Close", "Fechar"),
        ["AddToFavorites"] = ("Add to favorites", "Adicionar aos favoritos"),
        ["Language"] = ("Language", "Idioma"),
        // Edit station
        ["EditTitle"] = ("Edit station", "Editar rádio"),
        ["EditStation"] = ("Edit name and cover", "Editar nome e capa"),
        ["ChooseCover"] = ("Choose cover image…", "Escolher imagem de capa…"),
        ["ResetCover"] = ("Use the original cover", "Usar a capa original"),
        ["CoverHint"] = ("PNG or JPG. A square image looks best.", "PNG ou JPG. Uma imagem quadrada fica melhor."),
        ["StationNameLabel"] = ("Station name", "Nome da rádio"),
        ["ResetName"] = ("Back to \"{0}\"", "Voltar para \"{0}\""),
        ["NameHint"] = ("Used in the list, in Windows' media flyout and in new recording file names.", "Aparece na lista, no painel de mídia do Windows e no nome das novas gravações."),
        ["Save"] = ("Save", "Salvar"),
        ["ImageFilter"] = ("Images", "Imagens"),
        // Recordings
        ["ViewRecordings"] = ("View recordings", "Ver gravações"),
        ["NoRecordings"] = ("No recordings yet. Press Record while a station is playing.", "Nenhuma gravação ainda. Aperte Gravar enquanto uma rádio toca."),
        ["PickRecording"] = ("Pick a recording on the left", "Escolha uma gravação à esquerda"),
        ["DeleteRecording"] = ("Move to Recycle Bin", "Mover para a Lixeira"),
        ["DeleteRecordingConfirm"] = ("Move \"{0}\" to the Recycle Bin?", "Mover \"{0}\" para a Lixeira?"),
        ["ThisPart"] = ("Now in this part", "Neste trecho"),
        ["LegendSong"] = ("Song", "Música"),
        ["LegendTalk"] = ("Talk / break", "Locução / intervalo"),
        ["SyncAdjust"] = ("Adjust sync", "Ajustar sincronia"),
        ["SyncHint"] = ("Station song titles often arrive a few seconds early or late. Shift every mark here; it's saved to the .cue file.", "O nome da música costuma chegar alguns segundos antes ou depois. Desloque todas as marcações aqui; fica salvo no arquivo .cue."),
        ["ColStart"] = ("START", "INÍCIO"),
        ["ColSong"] = ("SONG", "MÚSICA"),
        ["ColLength"] = ("LENGTH", "DURAÇÃO"),
        ["ColCut"] = ("SPLIT", "CORTAR"),
        ["NoMarks"] = ("This recording has no song marks (the station didn't send song names).", "Esta gravação não tem músicas marcadas (a rádio não enviou o nome das músicas)."),
        ["ListenFrom"] = ("Listen from here", "Ouvir daqui"),
        ["TrackWord"] = ("Track", "Faixa"),
        ["SplitTracks"] = ("Save {0} tracks as separate files", "Salvar {0} faixas separadas"),
        ["SplitHint"] = ("Marks are saved as a .cue next to the recording. Split tracks keep the original quality.", "As marcações ficam num .cue ao lado da gravação. As faixas cortadas mantêm a qualidade original."),
        ["SplitDone"] = ("{0} tracks saved in \"{1}\"", "{0} faixas salvas em \"{1}\""),
        ["TracksFolder"] = ("tracks", "faixas"),
        // Liked songs
        ["LikedSongs"] = ("Liked songs", "Músicas favoritas"),
        ["LikedCount"] = ("{0} songs saved while listening", "{0} músicas salvas enquanto você ouvia"),
        ["ExportCsv"] = ("Export CSV", "Exportar CSV"),
        ["SearchSongs"] = ("Search song, artist or station", "Buscar música, artista ou rádio"),
        ["NoLikedSongs"] = ("No liked songs yet. Tap ♥ next to the song playing to save it here.", "Nenhuma música favoritada ainda. Toque no ♥ ao lado da música tocando para salvá-la aqui."),
        ["Unlike"] = ("Remove from liked songs", "Remover das favoritas"),
    };

    private string _lang = "en";

    private Loc()
    {
        _lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? "pt" : "en";
    }

    public string Language
    {
        get => _lang;
        set
        {
            var v = value == "pt" ? "pt" : "en";
            if (v == _lang) return;
            _lang = v;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public string this[string key]
    {
        get
        {
            if (!Strings.TryGetValue(key, out var s)) return key;
            return _lang == "pt" ? s.Pt : s.En;
        }
    }

    public static string T(string key) => I[key];

    public static string F(string key, params object[] args) => string.Format(I[key], args);

    public event PropertyChangedEventHandler? PropertyChanged;
}
