using static ScreenCord.Localization.Loc;

namespace ScreenCord.Localization;

/// <summary>
/// Todos os textos da interface, em português, inglês e espanhol (nessa ordem). Os que terminam em "Format" têm
/// <c>{0}</c> (e <c>{1}</c>) para <see cref="string.Format(string, object?)"/>. O XAML usa as chaves pelo nome
/// (<c>{l:Tr Close}</c>); um teste confere que toda chave usada no XAML existe aqui.
/// </summary>
public static class Strings
{
    // Geral
    public static string Close => T("Fechar", "Close", "Cerrar");
    public static string Cancel => T("Cancelar", "Cancel", "Cancelar");
    public static string Back => T("Voltar", "Back", "Volver");
    public static string Join => T("Entrar", "Join", "Entrar");
    public static string Loading => T("Carregando…", "Loading…", "Cargando…");
    public static string TryAgain => T("Tentar novamente", "Try again", "Intentar de nuevo");
    public static string Live => T("AO VIVO", "LIVE", "EN VIVO");
    public static string Connected => T("Conectado", "Connected", "Conectado");
    public static string Connecting => T("Conectando…", "Connecting…", "Conectando…");
    public static string Exit => T("Sair", "Quit", "Salir");

    // Barra de título
    public static string Minimize => T("Minimizar", "Minimize", "Minimizar");
    public static string Maximize => T("Maximizar", "Maximize", "Maximizar");
    public static string Restore => T("Restaurar", "Restore", "Restaurar");

    // Idioma
    public static string Language => T("Idioma", "Language", "Idioma");
    public static string LanguagePortuguese => "Português";
    public static string LanguageEnglish => "English";
    public static string LanguageSpanish => "Español";

    // Privacidade
    public static string Privacy => T("Privacidade", "Privacy", "Privacidad");
    public static string SendErrorReports => T("Enviar relatórios de erro", "Send error reports", "Enviar informes de errores");
    public static string ErrorReportsHint => T(
        "Avisos e erros do app, com a versão, o Windows, o hardware e um id aleatório da instalação. Nunca vão tokens, dados da conta nem a sua tela.",
        "App warnings and errors, with the version, Windows, hardware and a random install id. Never tokens, account data or your screen.",
        "Avisos y errores de la aplicación, con la versión, Windows, el hardware y un id aleatorio de la instalación. Nunca se envían tokens, datos de la cuenta ni tu pantalla.");

    // Atualizações (menu da engrenagem)
    public static string Updates => T("Atualizações", "Updates", "Actualizaciones");
    public static string AutoUpdate => T("Atualizar automaticamente", "Update automatically", "Actualizar automáticamente");
    public static string AutoUpdateHint => T(
        "Instala a versão nova ao abrir. Desligado, o app pergunta antes. Uma versão que o servidor não aceita mais precisa ser instalada para continuar.",
        "Installs the new version on startup. When off, the app asks first. A version the server no longer accepts must be installed to continue.",
        "Instala la nueva versión al abrir. Desactivado, la aplicación pregunta antes. Una versión que el servidor ya no acepta debe instalarse para continuar.");

    // Configurações (menu da engrenagem: idioma, privacidade, atualizações e "Sobre")
    public static string Settings => T("Configurações", "Settings", "Configuración");

    // Sobre
    public static string About => T("Sobre", "About", "Acerca de");
    public static string AboutScreenCord => T("Sobre o ScreenCord", "About ScreenCord", "Acerca de ScreenCord");
    public static string AboutTagline => T(
        "Compartilhe a tela com seus amigos em até 1080p 60 fps. Gratuito e de código aberto.",
        "Share your screen with friends at up to 1080p 60 fps. Free and open source.",
        "Comparte tu pantalla con tus amigos hasta 1080p 60 fps. Gratis y de código abierto.");
    public static string VersionFormat => T("Versão {0}", "Version {0}", "Versión {0}");
    public static string VersionDevFormat => T("Versão {0} (desenvolvimento)", "Version {0} (development)", "Versión {0} (desarrollo)");
    public static string WhatsNew => T("Novidades", "What's new", "Novedades");
    public static string ApiConnection => T("Conexão com o servidor", "Server connection", "Conexión con el servidor");
    public static string ApiServer => T("Servidor", "Server", "Servidor");
    public static string ApiStatus => T("Status", "Status", "Estado");
    public static string ApiVersion => T("Versão do servidor", "Server version", "Versión del servidor");
    public static string Realtime => T("Tempo real", "Real time", "Tiempo real");
    public static string ApiChecking => T("Verificando…", "Checking…", "Comprobando…");
    public static string ApiOnlineFormat => T("Online · {0} ms", "Online · {0} ms", "En línea · {0} ms");
    public static string ApiVersionNotAccepted => T(
        "Esta versão do app não é mais aceita",
        "This app version is no longer accepted",
        "Esta versión de la aplicación ya no se acepta");
    public static string ApiNotConfigured => T("Endereço inválido", "Invalid address", "Dirección no válida");
    public static string RealtimeReconnecting => T("Reconectando…", "Reconnecting…", "Reconectando…");
    public static string RealtimeOff => T("Desconectado", "Disconnected", "Desconectado");
    public static string TestAgain => T("Testar de novo", "Test again", "Probar de nuevo");
    public static string Website => T("Site", "Website", "Sitio web");
    public static string SourceCode => T("Código-fonte", "Source code", "Código fuente");
    public static string ReportProblem => T("Relatar um problema", "Report a problem", "Informar un problema");
    public static string OpenLogsFolder => T("Abrir pasta de logs", "Open logs folder", "Abrir carpeta de registros");
    public static string CopyDetails => T("Copiar informações", "Copy details", "Copiar información");
    public static string AboutLegal => T(
        "Licença GPL-3.0. Projeto não oficial, sem vínculo com o Discord.",
        "Licensed under GPL-3.0. Unofficial project, not affiliated with Discord.",
        "Licencia GPL-3.0. Proyecto no oficial, sin vínculo con Discord.");

    // Apoio no Ko-fi
    public static string Support => T("Apoiar", "Support", "Apoyar");
    public static string SupportOnKofi => T("Apoie no Ko-fi", "Support on Ko-fi", "Apóyanos en Ko-fi");
    public static string SupportScreenCordOnKofi => T("Apoiar o ScreenCord no Ko-fi", "Support ScreenCord on Ko-fi", "Apoyar a ScreenCord en Ko-fi");
    public static string SupportScreenCordOnKofiTooltip => T("Apoie o ScreenCord no Ko-fi", "Support ScreenCord on Ko-fi", "Apoya a ScreenCord en Ko-fi");
    public static string OpensKofi => T("Abre o Ko-fi no navegador", "Opens Ko-fi in your browser", "Abre Ko-fi en el navegador");

    // Tela inicial
    public static string LoadingSession => T("Carregando sessão…", "Loading session…", "Cargando sesión…");
    public static string ConnectWithDiscord => T("Conectar com Discord", "Sign in with Discord", "Conectar con Discord");
    public static string UseWithoutAccount => T("Usar sem conta (só salas locais)", "Use without an account (local rooms only)", "Usar sin cuenta (solo salas locales)");
    public static string LocalNamePlaceholder => T("Seu nome nas salas locais", "Your name in local rooms", "Tu nombre en las salas locales");
    public static string WaitingForBrowser => T("Aguardando a autorização no navegador…", "Waiting for authorization in the browser…", "Esperando la autorización en el navegador…");
    public static string LoginHint => T(
        "Entre com a sua conta do Discord para assistir e compartilhar telas com seus amigos.",
        "Sign in with your Discord account to watch and share screens with your friends.",
        "Inicia sesión con tu cuenta de Discord para ver y compartir pantallas con tus amigos.");
    public static string EnterLocalName => T(
        "Digite um nome para os outros verem nas salas locais.",
        "Enter a name for others to see in local rooms.",
        "Escribe un nombre para que los demás lo vean en las salas locales.");

    // Cabeçalho da conta
    public static string Friends => T("Amigos", "Friends", "Amigos");
    public static string RefreshProfile => T("Atualizar perfil", "Refresh profile", "Actualizar perfil");
    public static string Disconnect => T("Desconectar", "Sign out", "Desconectar");
    public static string AccountConnected => T("Conta conectada", "Account connected", "Cuenta conectada");
    public static string NoAccountLocalOnly => T("Sem conta · só salas locais", "No account · local rooms only", "Sin cuenta · solo salas locales");
    public static string DisconnectQuestion => T("Desconectar conta?", "Sign out of your account?", "¿Desconectar la cuenta?");

    // Tela principal
    public static string NoScreenOpen => T("Nenhuma tela aberta", "No screen open", "Ninguna pantalla abierta");
    public static string NoScreenHint => T(
        "Quando alguém da sala compartilhar a tela, clique em AO VIVO ao lado do nome para assistir aqui.",
        "When someone in the room shares their screen, click LIVE next to their name to watch it here.",
        "Cuando alguien de la sala comparta su pantalla, haz clic en EN VIVO junto a su nombre para verla aquí.");

    // Grupos
    public static string GroupsHeader => T("GRUPOS", "GROUPS", "GRUPOS");
    public static string CreateGroupOrRoom => T("Criar grupo ou sala local", "Create a group or local room", "Crear grupo o sala local");
    public static string CreateGroup => T("Criar grupo", "Create group", "Crear grupo");
    public static string CreateLocalRoom => T("Criar sala local", "Create local room", "Crear sala local");
    public static string GroupCreator => T("Criador do grupo", "Group creator", "Creador del grupo");
    public static string ConnectedSuffix => T(" · conectado", " · connected", " · conectado");
    public static string ToggleConnected => T("Mostrar ou esconder quem está conectado", "Show or hide who is connected", "Mostrar u ocultar quién está conectado");
    public static string JoinGroupFormat => T("Conectar ao grupo {0}", "Join group {0}", "Conectarse al grupo {0}");
    public static string LocalBadge => "LOCAL";
    public static string LocalBadgeTooltip => T(
        "Sala local: a tela vai direto entre os computadores da rede",
        "Local room: the screen goes straight between computers on the network",
        "Sala local: la pantalla va directo entre las computadoras de la red");
    public static string ViewMembers => T("Ver integrantes", "View members", "Ver integrantes");
    public static string CopyIp => T("Copiar o IP", "Copy IP", "Copiar la IP");
    public static string CopyFormat => T("Copiar {0}", "Copy {0}", "Copiar {0}");
    public static string Copied => T("Copiado", "Copied", "Copiado");
    public static string MembersHeader => T("INTEGRANTES", "MEMBERS", "INTEGRANTES");
    public static string Invite => T("Convidar", "Invite", "Invitar");
    public static string Leave => T("Sair", "Leave", "Salir");
    public static string YouAreInThisRoom => T("Você está conectado a esta sala", "You are connected to this room", "Estás conectado a esta sala");
    public static string WatchScreenOfFormat => T("Assistir à tela de {0}", "Watch {0}'s screen", "Ver la pantalla de {0}");
    public static string LocalAddressesHeader => T(
        "Quem não vê a sala na lista entra por um destes IPs:",
        "Anyone who doesn't see the room in the list can join with one of these IPs:",
        "Quien no vea la sala en la lista puede entrar con una de estas IP:");
    public static string LocalAddressHeader => T("Endereço da sala:", "Room address:", "Dirección de la sala:");
    public static string EndRoom => T("Encerrar", "End", "Finalizar");
    public static string DeleteGroup => T("Excluir", "Delete", "Eliminar");
    public static string EndRoomQuestion => T("Encerrar a sala?", "End the room?", "¿Finalizar la sala?");
    public static string DeleteGroupQuestion => T("Excluir o grupo?", "Delete the group?", "¿Eliminar el grupo?");
    public static string EndRoomSuffix => T(" será encerrada para todos que estão nela.", " will be ended for everyone in it.", " se finalizará para todos los que están en ella.");
    public static string DeleteGroupSuffix => T(" será excluído para todos os integrantes.", " will be deleted for all members.", " se eliminará para todos los integrantes.");
    public static string LeaveGroupQuestion => T("Sair do grupo?", "Leave the group?", "¿Salir del grupo?");
    public static string LeaveGroupPrefix => T("Você sai de ", "You'll leave ", "Saldrás de ");
    public static string LeaveGroupSuffix => T(
        ". Para voltar, alguém do grupo precisa convidar você.",
        ". To come back, someone in the group has to invite you.",
        ". Para volver, alguien del grupo tiene que invitarte.");
    public static string NoLocalRooms => T(
        "Nenhuma sala local na rede. Clique em + para criar uma ou entrar numa pelo IP.",
        "No local rooms on the network. Click + to create one or join one by IP.",
        "No hay salas locales en la red. Haz clic en + para crear una o entrar a una por IP.");
    public static string NoGroups => T(
        "Nenhum grupo ou sala ainda. Clique em + para criar um grupo com amigos, criar uma sala local ou entrar numa pelo IP.",
        "No groups or rooms yet. Click + to create a group with friends, create a local room or join one by IP.",
        "Todavía no hay grupos ni salas. Haz clic en + para crear un grupo con amigos, crear una sala local o entrar a una por IP.");
    public static string ConnectingToServer => T("Conectando ao servidor…", "Connecting to the server…", "Conectando al servidor…");
    public static string Reconnecting => T("Conexão perdida. Reconectando…", "Connection lost. Reconnecting…", "Conexión perdida. Reconectando…");
    public static string NoRealtime => T("Sem conexão em tempo real.", "No real-time connection.", "Sin conexión en tiempo real.");
    public static string FirewallWarning => T(
        "O Firewall do Windows pode impedir que os outros entrem na sua sala ou vejam a sua tela. Libere o ScreenCord para as redes locais e VPNs (o Windows vai pedir permissão).",
        "Windows Firewall may stop others from joining your room or seeing your screen. Allow ScreenCord on local networks and VPNs (Windows will ask for permission).",
        "El Firewall de Windows puede impedir que otros entren a tu sala o vean tu pantalla. Permite ScreenCord en las redes locales y VPN (Windows pedirá permiso).");
    public static string AllowInFirewall => T("Liberar no firewall", "Allow in firewall", "Permitir en el firewall");
    public static string NotNow => T("Agora não", "Not now", "Ahora no");
    public static string FirewallNotCreated => T(
        "A regra do firewall não foi criada. Aceite o pedido de permissão do Windows para liberar as salas locais.",
        "The firewall rule wasn't created. Accept the Windows permission prompt to allow local rooms.",
        "No se creó la regla del firewall. Acepta la solicitud de permiso de Windows para permitir las salas locales.");

    public static string MembersSummary(int members, int connected) =>
        string.Format(Culture, members == 1 ? T("{0} integrante", "{0} member", "{0} integrante") : T("{0} integrantes", "{0} members", "{0} integrantes"), members) +
        " · " +
        (connected == 0 ? T("ninguém conectado", "nobody connected", "nadie conectado")
            : string.Format(Culture, connected == 1 ? T("{0} conectado", "{0} connected", "{0} conectado") : T("{0} conectados", "{0} connected", "{0} conectados"), connected));

    // Criar grupo ou sala
    public static string GroupWithFriends => T("Grupo com amigos", "Group with friends", "Grupo con amigos");
    public static string NeedsDiscordAccount => T("Precisa de conta do Discord.", "Requires a Discord account.", "Requiere una cuenta de Discord.");
    public static string LocalRoom => T("Sala local", "Local room", "Sala local");
    public static string LocalRoomHint => T(
        "Para quem está na mesma rede. A tela vai direto entre os computadores e funciona mesmo sem o servidor. Ela fica aberta enquanto você estiver nela.",
        "For people on the same network. The screen goes straight between computers and works even without the server. It stays open while you're in it.",
        "Para quienes están en la misma red. La pantalla va directo entre las computadoras y funciona incluso sin el servidor. Queda abierta mientras estés en ella.");
    public static string GroupHint => T(
        "Possibilita compartilhar a tela para qualquer amigo online, de qualquer lugar. Qualidade limitada.",
        "Lets you share your screen with any online friend, from anywhere. Limited quality.",
        "Permite compartir la pantalla con cualquier amigo en línea, desde cualquier lugar. Calidad limitada.");
    public static string NamePlaceholder => T("Nome", "Name", "Nombre");
    public static string GroupOrRoomName => T("Nome do grupo ou da sala", "Group or room name", "Nombre del grupo o de la sala");
    public static string Create => T("Criar", "Create", "Crear");
    public static string EnterGroupName => T("Digite um nome para o grupo.", "Enter a name for the group.", "Escribe un nombre para el grupo.");
    public static string JoinByIpTitle => T("Entrar numa sala local pelo IP", "Join a local room by IP", "Entrar a una sala local por IP");
    public static string JoinByIpHint => T(
        "As salas da mesma rede aparecem sozinhas na lista. Se a sua não aparecer, peça o IP a quem hospeda.",
        "Rooms on the same network show up in the list on their own. If yours doesn't, ask the host for their IP.",
        "Las salas de la misma red aparecen solas en la lista. Si la tuya no aparece, pide la IP a quien la aloja.");
    public static string JoinByIpPlaceholder => T("Ex.: 25.1.2.3 ou 192.168.0.10", "E.g. 25.1.2.3 or 192.168.0.10", "Ej.: 25.1.2.3 o 192.168.0.10");
    public static string HostIp => T("IP de quem hospeda a sala", "IP of the room's host", "IP de quien aloja la sala");

    // Compartilhamento (lateral)
    public static string ShareScreen => T("Compartilhar tela", "Share screen", "Compartir pantalla");
    public static string LeaveRoom => T("Sair da sala", "Leave room", "Salir de la sala");
    public static string ChangeSettings => T("Alterar configurações", "Change settings", "Cambiar configuración");
    public static string ChangeShareSettings => T("Alterar configurações do compartilhamento", "Change sharing settings", "Cambiar la configuración de la transmisión");
    public static string StopSharing => T("Parar de compartilhar", "Stop sharing", "Dejar de compartir");
    public static string ApplyingSettings => T("Aplicando as novas configurações…", "Applying the new settings…", "Aplicando la nueva configuración…");
    public static string StartingShare => T("Iniciando o compartilhamento…", "Starting to share…", "Iniciando la transmisión…");
    public static string WithAudioSuffix => T(" · com áudio", " · with audio", " · con audio");
    public static string ShareStoppedServerLost => T(
        "O compartilhamento de tela parou porque a conexão com o servidor caiu.",
        "Screen sharing stopped because the connection to the server dropped.",
        "La transmisión de pantalla se detuvo porque se cayó la conexión con el servidor.");
    public static string NotSharingAnymoreFormat => T("{0} não está mais compartilhando a tela.", "{0} is no longer sharing their screen.", "{0} ya no está compartiendo la pantalla.");

    // Amigos
    public static string FriendsDescription => T(
        "Amigos no ScreenCord: pessoas que também entraram aqui com o Discord. Não é a sua lista de amigos do Discord.",
        "Friends on ScreenCord: people who also signed in here with Discord. This isn't your Discord friends list.",
        "Amigos en ScreenCord: personas que también entraron aquí con Discord. No es tu lista de amigos de Discord.");
    public static string InviteToFormat => T("Convidar para {0}", "Invite to {0}", "Invitar a {0}");
    public static string InviteDescription => T(
        "Convide seus amigos para o grupo. Quem ainda não é seu amigo pode ser buscado e adicionado aqui.",
        "Invite your friends to the group. People who aren't your friends yet can be searched for and added here.",
        "Invita a tus amigos al grupo. Quienes todavía no son tus amigos se pueden buscar y agregar aquí.");
    public static string SearchPlaceholder => T("Buscar usuário para adicionar (ex.: @fulano)", "Search for a user to add (e.g. @someone)", "Buscar usuario para agregar (ej.: @fulano)");
    public static string SearchUsers => T("Buscar usuários", "Search users", "Buscar usuarios");
    public static string Search => T("Buscar", "Search", "Buscar");
    public static string BackToFriends => T("Voltar aos amigos", "Back to friends", "Volver a los amigos");
    public static string IncomingTab => T("Recebidas", "Received", "Recibidas");
    public static string OutgoingTab => T("Enviadas", "Sent", "Enviadas");
    public static string IncomingRequests => T("Solicitações recebidas", "Received requests", "Solicitudes recibidas");
    public static string OutgoingRequests => T("Solicitações enviadas", "Sent requests", "Solicitudes enviadas");
    public static string Add => T("Adicionar", "Add", "Agregar");
    public static string Accept => T("Aceitar", "Accept", "Aceptar");
    public static string Decline => T("Recusar", "Decline", "Rechazar");
    public static string CancelRequest => T("Cancelar pedido", "Cancel request", "Cancelar solicitud");
    public static string Invited => T("Convidado", "Invited", "Invitado");
    public static string Remove => T("Remover", "Remove", "Eliminar");
    public static string RemoveFriendPrefix => T("Remover ", "Remove ", "¿Eliminar a ");
    public static string RemoveFriendSuffix => T(" dos seus amigos?", " from your friends?", " de tus amigos?");
    public static string SearchResultsHeader => T("RESULTADOS DA BUSCA", "SEARCH RESULTS", "RESULTADOS DE LA BÚSQUEDA");
    public static string Searching => T("Buscando…", "Searching…", "Buscando…");
    public static string YourFriendsHeader => T("SEUS AMIGOS — ", "YOUR FRIENDS — ", "TUS AMIGOS — ");
    public static string IncomingHeader => T("SOLICITAÇÕES RECEBIDAS — ", "RECEIVED REQUESTS — ", "SOLICITUDES RECIBIDAS — ");
    public static string OutgoingHeader => T("SOLICITAÇÕES ENVIADAS — ", "SENT REQUESTS — ", "SOLICITUDES ENVIADAS — ");
    public static string NoFriends => T(
        "Você ainda não tem amigos aqui. Busque alguém pelo nome de usuário para enviar uma solicitação.",
        "You don't have any friends here yet. Search for someone by username to send a request.",
        "Todavía no tienes amigos aquí. Busca a alguien por su nombre de usuario para enviarle una solicitud.");
    public static string NoIncoming => T("Nenhuma solicitação de amizade recebida.", "No friend requests received.", "No recibiste solicitudes de amistad.");
    public static string NoOutgoing => T(
        "Nenhuma solicitação pendente. Busque alguém pelo nome de usuário para enviar uma.",
        "No pending requests. Search for someone by username to send one.",
        "No hay solicitudes pendientes. Busca a alguien por su nombre de usuario para enviarle una.");
    public static string SearchMinLengthFormat => T("Digite pelo menos {0} caracteres para buscar.", "Type at least {0} characters to search.", "Escribe al menos {0} caracteres para buscar.");
    public static string NoUserFoundFormat => T(
        "Nenhum usuário do ScreenCord encontrado para \"{0}\". A pessoa precisa ter entrado no sistema com o Discord.",
        "No ScreenCord user found for \"{0}\". The person needs to have signed in with Discord.",
        "No se encontró ningún usuario de ScreenCord para \"{0}\". La persona tiene que haber entrado con Discord.");

    // Escolha do que compartilhar
    public static string ChangeSharing => T("Alterar compartilhamento", "Change sharing", "Cambiar transmisión");
    public static string Apply => T("Aplicar", "Apply", "Aplicar");
    public static string Share => T("Compartilhar", "Share", "Compartir");
    public static string ChangeShareHint => T(
        "Quem está assistindo continua vendo, já com as novas configurações.",
        "Viewers keep watching, now with the new settings.",
        "Quienes están mirando siguen viendo, ya con la nueva configuración.");
    public static string ScreenKind => T("Tela", "Screen", "Pantalla");
    public static string WindowKind => T("Janela", "Window", "Ventana");
    public static string CameraKind => T("Câmera", "Camera", "Cámara");
    public static string TurnOnCamera => T("Ligar a câmera", "Turn on camera", "Encender la cámara");
    public static string TurnOffCamera => T("Desligar a câmera", "Turn off camera", "Apagar la cámara");
    public static string CameraPreviewHeader => T("Ativar a câmera", "Turn on the camera", "Activar la cámara");
    public static string CameraPreviewHint => T(
        "É assim que a sala vai ver você. Nada é transmitido até você ativar a câmera.",
        "This is how the room will see you. Nothing is sent until you turn the camera on.",
        "Así te verá la sala. No se transmite nada hasta que actives la cámara.");
    public static string ActivateCamera => T("Ativar câmera", "Turn on camera", "Activar cámara");
    public static string CameraPreviewFailed => T(
        "Não foi possível abrir esta câmera. Ela pode estar em uso por outro programa.",
        "Couldn't open this camera. It may be in use by another program.",
        "No se pudo abrir esta cámara. Puede estar en uso por otro programa.");
    public static string WatchCameraOfFormat => T("Assistir à câmera de {0}", "Watch {0}'s camera", "Ver la cámara de {0}");
    public static string NoCamerasFound => T(
        "Nenhuma câmera encontrada. Conecte uma e veja se o Windows permite o acesso das apps à câmera (Configurações → Privacidade → Câmera).",
        "No camera found. Plug one in and check that Windows lets apps use the camera (Settings → Privacy → Camera).",
        "No se encontró ninguna cámara. Conecta una y revisa que Windows permita a las aplicaciones usar la cámara (Configuración → Privacidad → Cámara).");
    public static string SharedCameraDisconnected => T("A câmera foi desconectada ou parou de enviar imagem.", "The camera was disconnected or stopped sending video.", "La cámara se desconectó o dejó de enviar imagen.");
    public static string ViewerCameraTitleFormat => T("Câmera de {0} · {1}", "{0}'s camera · {1}", "Cámara de {0} · {1}");

    // Configuração do grupo (o que ele aceita transmitir)
    public static string GroupAllowsHeader => T("O GRUPO ACEITA", "THE GROUP ALLOWS", "EL GRUPO ACEPTA");
    public static string AllowScreenShareOption => T("Compartilhamento de tela", "Screen sharing", "Transmisión de pantalla");
    public static string AllowCameraOption => T("Câmera (webcam)", "Camera (webcam)", "Cámara (webcam)");
    public static string GroupSharingOff => T(
        "Este grupo não aceita transmitir tela nem câmera. Quem o criou pode mudar isso nos integrantes.",
        "This group doesn't allow sharing the screen or a camera. Whoever created it can change that in the members list.",
        "Este grupo no acepta transmitir pantalla ni cámara. Quien lo creó puede cambiarlo en la lista de integrantes.");
    public static string GroupNoScreenShare => T("Este grupo não aceita compartilhar a tela.", "This group doesn't allow screen sharing.", "Este grupo no acepta transmitir la pantalla.");
    public static string GroupNoCamera => T("Este grupo não aceita câmera.", "This group doesn't allow cameras.", "Este grupo no acepta cámaras.");
    public static string OnlyHostChangesRoom => T("Só quem hospeda a sala pode mudar as configurações dela.", "Only the room's host can change its settings.", "Solo quien aloja la sala puede cambiar su configuración.");
    public static string ShareAScreen => T("Compartilhar uma tela", "Share a screen", "Compartir una pantalla");
    public static string ShareAWindow => T("Compartilhar uma janela", "Share a window", "Compartir una ventana");
    public static string RefreshList => T("Atualizar lista", "Refresh list", "Actualizar lista");
    public static string WhatToShare => T("O que compartilhar", "What to share", "Qué compartir");
    public static string ResolutionHeader => T("RESOLUÇÃO", "RESOLUTION", "RESOLUCIÓN");
    public static string FrameRateHeader => T("TAXA DE QUADROS", "FRAME RATE", "FOTOGRAMAS POR SEGUNDO");
    public static string ShareAudio => T("Compartilhar áudio", "Share audio", "Compartir audio");
    public static string WindowAudioHint => T("Só o áudio do programa da janela.", "Only the audio from the window's program.", "Solo el audio del programa de la ventana.");
    public static string ScreenAudioHint => T("Todo o áudio do computador, inclusive chamadas.", "All of the computer's audio, including calls.", "Todo el audio de la computadora, incluidas las llamadas.");
    public static string NoScreensFound => T("Nenhuma tela encontrada.", "No screens found.", "No se encontraron pantallas.");
    public static string NoWindowsToShare => T("Nenhuma janela aberta para compartilhar.", "No open windows to share.", "No hay ventanas abiertas para compartir.");
    public static string DirectOnly => T(
        "Só nas salas locais, com a mídia direto entre os computadores.",
        "Only in local rooms, with media going straight between computers.",
        "Solo en las salas locales, con el contenido directo entre las computadoras.");
    public static string AutoQualityFormat => T("Qualidade automática, até {0} Mbps · {1}", "Automatic quality, up to {0} Mbps · {1}", "Calidad automática, hasta {0} Mbps · {1}");
    public static string DirectBetweenComputers => T("direto entre os computadores", "straight between computers", "directo entre las computadoras");
    public static string ThroughServer => T("pelo servidor", "through the server", "a través del servidor");
    public static string ScreenNameFormat => T("Tela {0}", "Screen {0}", "Pantalla {0}");
    public static string PrimarySuffix => T(" (principal)", " (primary)", " (principal)");

    // Assistindo
    public static string ViewerTitleFormat => T("Tela de {0} · {1}", "{0}'s screen · {1}", "Pantalla de {0} · {1}");
    public static string ShareEnded => T("Compartilhamento encerrado.", "Sharing ended.", "Transmisión finalizada.");
    public static string WaitingForImage => T("Aguardando imagem…", "Waiting for video…", "Esperando imagen…");
    public static string Mute => T("Silenciar", "Mute", "Silenciar");
    public static string MuteTooltip => T("Silenciar (M)", "Mute (M)", "Silenciar (M)");
    public static string Volume => T("Volume", "Volume", "Volumen");
    public static string FullScreen => T("Tela cheia", "Full screen", "Pantalla completa");
    public static string FullScreenTooltip => T("Tela cheia (F11)", "Full screen (F11)", "Pantalla completa (F11)");
    public static string ExitFullScreen => T("Sair da tela cheia", "Exit full screen", "Salir de pantalla completa");
    public static string ExitFullScreenTooltip => T("Sair da tela cheia (Esc)", "Exit full screen (Esc)", "Salir de pantalla completa (Esc)");
    public static string StopWatching => T("Parar de assistir", "Stop watching", "Dejar de ver");
    public static string PinTooltip => T("Fixar como principal", "Pin as main", "Fijar como principal");
    public static string UnpinTooltip => T("Desafixar", "Unpin", "Desfijar");

    // Atualização
    public static string UpdatingTitle => T("Atualizando o ScreenCord", "Updating ScreenCord", "Actualizando ScreenCord");
    public static string CheckingForUpdates => T("Procurando atualizações…", "Checking for updates…", "Buscando actualizaciones…");
    public static string DownloadingVersionFormat => T("Baixando a versão {0}…", "Downloading version {0}…", "Descargando la versión {0}…");
    public static string InstallingVersionFormat => T(
        "Instalando a versão {0}. O ScreenCord abre de novo em seguida.",
        "Installing version {0}. ScreenCord will reopen right after.",
        "Instalando la versión {0}. ScreenCord se abrirá de nuevo enseguida.");
    public static string UpdateAvailableFormat => T(
        "A versão {0} do ScreenCord está disponível (você usa a {1}). Quer atualizar agora?",
        "ScreenCord {0} is available (you have {1}). Update now?",
        "La versión {0} de ScreenCord está disponible (tienes la {1}). ¿Quieres actualizar ahora?");
    public static string UpdateRequiredFormat => T(
        "Esta versão do ScreenCord ({1}) não é mais aceita pelo servidor. Atualize para a {0} para continuar usando o app.",
        "This version of ScreenCord ({1}) is no longer accepted by the server. Update to {0} to keep using the app.",
        "El servidor ya no acepta esta versión de ScreenCord ({1}). Actualiza a la {0} para seguir usando la aplicación.");
    public static string UpdateRequiredFailedFormat => T(
        "Não foi possível instalar a versão {0}, e esta versão não é mais aceita pelo servidor. Baixe a versão nova em {1}. O ScreenCord vai fechar.",
        "Version {0} couldn't be installed, and this version is no longer accepted by the server. Download the new version at {1}. ScreenCord will close.",
        "No se pudo instalar la versión {0}, y el servidor ya no acepta esta versión. Descarga la nueva versión en {1}. ScreenCord se cerrará.");
    public static string UpdateNow => T("Atualizar agora", "Update now", "Actualizar ahora");
    public static string Update => T("Atualizar", "Update", "Actualizar");
    public static string SkipThisVersion => T("Pular esta versão", "Skip this version", "Omitir esta versión");
    public static string CheckForUpdatesAction => T("Procurar atualizações", "Check for updates", "Buscar actualizaciones");
    public static string NoUpdateFound => T("Nenhuma versão nova encontrada.", "No new version found.", "No se encontró ninguna versión nueva.");
    public static string ClientOutdated => T(
        "Esta versão do ScreenCord não é mais aceita pelo servidor. Atualize o aplicativo.",
        "This version of ScreenCord is no longer accepted by the server. Please update the app.",
        "El servidor ya no acepta esta versión de ScreenCord. Actualiza la aplicación.");

    // Página do navegador depois do login
    public static string BrowserNotFoundTitle => T("Não encontrado", "Not found", "No encontrado");
    public static string BrowserNotFound => T("Este endereço não existe.", "This address doesn't exist.", "Esta dirección no existe.");
    public static string BrowserBadRequestTitle => T("Requisição inválida", "Invalid request", "Solicitud no válida");
    public static string BrowserBadRequest => T("Nenhum dado de autorização foi recebido.", "No authorization data was received.", "No se recibieron datos de autorización.");
    public static string BrowserDoneTitle => T("Tudo certo", "All set", "Todo listo");
    public static string BrowserDone => T("Você já pode fechar esta aba e voltar ao aplicativo ScreenCord.", "You can close this tab and go back to the ScreenCord app.", "Ya puedes cerrar esta pestaña y volver a la aplicación ScreenCord.");
    public static string BrowserNotCompletedTitle => T("Autorização não concluída", "Authorization not completed", "Autorización no completada");
    public static string BrowserNotCompleted => T("Você pode fechar esta aba e voltar ao aplicativo ScreenCord.", "You can close this tab and go back to the ScreenCord app.", "Puedes cerrar esta pestaña y volver a la aplicación ScreenCord.");

    // Erros
    public static string OperationCanceled => T("Operação cancelada.", "Operation canceled.", "Operación cancelada.");
    public static string UnexpectedError => T("Ocorreu um erro inesperado. Tente novamente.", "Something unexpected went wrong. Please try again.", "Ocurrió un error inesperado. Inténtalo de nuevo.");
    public static string ApiNetwork => T(
        "Não foi possível conectar ao servidor do ScreenCord. Verifique se a API está em execução e a sua conexão.",
        "Couldn't connect to the ScreenCord server. Check that the API is running and your connection.",
        "No se pudo conectar al servidor de ScreenCord. Verifica que la API esté en ejecución y tu conexión.");
    public static string ApiTimeout => T("O servidor do ScreenCord demorou demais para responder. Tente novamente.", "The ScreenCord server took too long to respond. Please try again.", "El servidor de ScreenCord tardó demasiado en responder. Inténtalo de nuevo.");
    public static string ApiUnauthorized => T(
        "O servidor do ScreenCord não aceitou o seu login. Desconecte e conecte sua conta novamente.",
        "The ScreenCord server didn't accept your sign-in. Sign out and sign in again.",
        "El servidor de ScreenCord no aceptó tu inicio de sesión. Desconéctate y vuelve a conectar tu cuenta.");
    public static string ApiRateLimited => T(
        "Muitas requisições ao servidor do ScreenCord. Aguarde um instante e tente novamente.",
        "Too many requests to the ScreenCord server. Wait a moment and try again.",
        "Demasiadas solicitudes al servidor de ScreenCord. Espera un momento e inténtalo de nuevo.");
    public static string ApiUnavailable => T(
        "O servidor do ScreenCord está indisponível no momento. Tente novamente mais tarde.",
        "The ScreenCord server is unavailable right now. Please try again later.",
        "El servidor de ScreenCord no está disponible en este momento. Inténtalo más tarde.");
    public static string ApiUnexpected => T("O servidor do ScreenCord retornou uma resposta inesperada. Tente novamente.", "The ScreenCord server returned an unexpected response. Please try again.", "El servidor de ScreenCord devolvió una respuesta inesperada. Inténtalo de nuevo.");
    public static string DiscordNetwork => T(
        "Não foi possível conectar ao Discord. Verifique sua conexão com a internet e tente novamente.",
        "Couldn't connect to Discord. Check your internet connection and try again.",
        "No se pudo conectar a Discord. Verifica tu conexión a internet e inténtalo de nuevo.");
    public static string DiscordTimeout => T("O Discord demorou demais para responder. Tente novamente.", "Discord took too long to respond. Please try again.", "Discord tardó demasiado en responder. Inténtalo de nuevo.");
    public static string DiscordDenied => T("A autorização foi negada no Discord. Nenhum dado foi acessado.", "Authorization was denied on Discord. No data was accessed.", "Se denegó la autorización en Discord. No se accedió a ningún dato.");
    public static string DiscordInvalidState => T(
        "A resposta de autorização não pôde ser validada e foi descartada por segurança. Tente conectar novamente.",
        "The authorization response couldn't be validated and was discarded for security. Try signing in again.",
        "No se pudo validar la respuesta de autorización y se descartó por seguridad. Intenta conectar de nuevo.");
    public static string DiscordInvalidClient => T(
        "O Discord rejeitou o aplicativo (invalid_client). Verifique o Client ID e se a opção \"Public Client\" está ativada no Developer Portal.",
        "Discord rejected the app (invalid_client). Check the Client ID and whether \"Public Client\" is enabled in the Developer Portal.",
        "Discord rechazó la aplicación (invalid_client). Verifica el Client ID y si la opción \"Public Client\" está activada en el Developer Portal.");
    public static string DiscordRejectedFormat => T("O Discord rejeitou a autenticação{0}. Tente conectar novamente.", "Discord rejected the sign-in{0}. Try signing in again.", "Discord rechazó la autenticación{0}. Intenta conectar de nuevo.");
    public static string DiscordUnauthorized => T("Sua sessão expirou ou foi revogada. Conecte sua conta novamente.", "Your session expired or was revoked. Sign in again.", "Tu sesión expiró o fue revocada. Vuelve a conectar tu cuenta.");
    public static string DiscordRateLimitedFormat => T("Muitas requisições. Aguarde {0} s e tente novamente.", "Too many requests. Wait {0} s and try again.", "Demasiadas solicitudes. Espera {0} s e inténtalo de nuevo.");
    public static string DiscordRateLimited => T("Muitas requisições. Aguarde um instante e tente novamente.", "Too many requests. Wait a moment and try again.", "Demasiadas solicitudes. Espera un momento e inténtalo de nuevo.");
    public static string DiscordUnavailable => T("A API do Discord está indisponível no momento. Tente novamente mais tarde.", "The Discord API is unavailable right now. Please try again later.", "La API de Discord no está disponible en este momento. Inténtalo más tarde.");
    public static string DiscordInvalidResponse => T("O Discord retornou uma resposta inesperada. Tente novamente.", "Discord returned an unexpected response. Please try again.", "Discord devolvió una respuesta inesperada. Inténtalo de nuevo.");
    public static string DiscordUnexpected => T("Ocorreu um erro inesperado ao falar com o Discord. Tente novamente.", "Something unexpected went wrong while talking to Discord. Please try again.", "Ocurrió un error inesperado al comunicarse con Discord. Inténtalo de nuevo.");

    // Erros do compartilhamento e da mídia
    public static string CaptureNotSupported => T(
        "Este Windows não permite capturar a tela (é preciso o Windows 10 versão 1903 ou mais recente).",
        "This version of Windows can't capture the screen (Windows 10 version 1903 or later is required).",
        "Esta versión de Windows no permite capturar la pantalla (se necesita Windows 10 versión 1903 o posterior).");
    public static string ChosenWindowClosed => T("A janela escolhida foi fechada.", "The chosen window was closed.", "Se cerró la ventana elegida.");
    public static string AudioCaptureFailed => T(
        "Não foi possível capturar o áudio: o compartilhamento segue sem som.",
        "Couldn't capture the audio: sharing continues without sound.",
        "No se pudo capturar el audio: la transmisión sigue sin sonido.");
    public static string NewSettingsFailed => T(
        "Não foi possível aplicar as novas configurações, e o compartilhamento parou.",
        "Couldn't apply the new settings, and sharing stopped.",
        "No se pudo aplicar la nueva configuración y la transmisión se detuvo.");
    public static string ServerEndedShare => T("O servidor encerrou o compartilhamento.", "The server ended the sharing.", "El servidor finalizó la transmisión.");
    public static string SharedScreenDisconnected => T("A tela compartilhada foi desconectada.", "The shared screen was disconnected.", "Se desconectó la pantalla compartida.");
    public static string SharedWindowClosed => T("A janela compartilhada foi fechada.", "The shared window was closed.", "Se cerró la ventana compartida.");
    public static string CaptureOrEncoderFailed => T(
        "O compartilhamento parou por um erro na captura ou no encoder de vídeo.",
        "Sharing stopped because of an error in the capture or the video encoder.",
        "La transmisión se detuvo por un error en la captura o en el codificador de video.");
    public static string ShareAlreadyEnded => T("Este compartilhamento de tela já terminou.", "This screen share has already ended.", "Esta transmisión de pantalla ya terminó.");
    public static string JoinGroupRoomToWatch => T("Entre na sala do grupo para assistir.", "Join the group's room to watch.", "Entra a la sala del grupo para mirar.");
    public static string ShareNotStartedOnServer => T("O compartilhamento não foi iniciado no servidor.", "The sharing wasn't started on the server.", "La transmisión no se inició en el servidor.");
    public static string MediaChannelFailed => T("Não foi possível abrir o canal de vídeo com a API central.", "Couldn't open the video channel with the central API.", "No se pudo abrir el canal de video con la API central.");
    public static string MediaPeerServer => T("com a API central", "with the central API", "con la API central");
    public static string MediaPeerComputerFormat => T("com o computador de {0}", "with {0}'s computer", "con la computadora de {0}");
    public static string DirectConnectFailedFormat => T(
        "Não foi possível conectar direto ao computador de {0}. Confira se vocês estão na mesma rede (LAN, Hamachi, Radmin ou Tailscale) e se o Firewall do Windows de quem compartilha liberou o ScreenCord.",
        "Couldn't connect directly to {0}'s computer. Check that you're on the same network (LAN, Hamachi, Radmin or Tailscale) and that the sharer's Windows Firewall allows ScreenCord.",
        "No se pudo conectar directo a la computadora de {0}. Verifica que estén en la misma red (LAN, Hamachi, Radmin o Tailscale) y que el Firewall de Windows de quien comparte permita ScreenCord.");
    public static string MediaDroppedFormat => T("A conexão de vídeo {0} caiu.", "The video connection {0} dropped.", "Se cayó la conexión de video {0}.");
    public static string MediaPacketTooLargeFormat => T("A conexão de vídeo {0} enviou um pacote grande demais.", "The video connection {0} sent a packet that was too large.", "La conexión de video {0} envió un paquete demasiado grande.");
    public static string NotInGroup => T("Você não faz parte deste grupo.", "You're not part of this group.", "No formas parte de este grupo.");
    public static string JoinGroupRoomToShare => T("Entre na sala de um grupo para compartilhar a tela.", "Join a group's room to share your screen.", "Entra a la sala de un grupo para compartir la pantalla.");

    // Salas locais
    public static string ThisComputer => T("Este computador", "This computer", "Esta computadora");
    public static string Network => T("Rede", "Network", "Red");
    public static string LocalNetwork => T("Rede local", "Local network", "Red local");
    public static string ComputerName => T("Nome do computador", "Computer name", "Nombre de la computadora");
    public static string SomeoneSharing => T("quem compartilha", "the sharer", "quien comparte");
    public static string RoomRefusedEntry => T("A sala recusou a entrada.", "The room refused the entry.", "La sala rechazó la entrada.");
    public static string NotAScreenCordRoom => T("O outro computador não respondeu como uma sala do ScreenCord.", "The other computer didn't respond as a ScreenCord room.", "La otra computadora no respondió como una sala de ScreenCord.");
    public static string CertificateMismatchFormat => T(
        "O computador em {0} não é o que anunciou a sala (o certificado não confere). Peça o endereço para quem hospeda e entre pelo IP.",
        "The computer at {0} isn't the one that announced the room (the certificate doesn't match). Ask the host for the address and join by IP.",
        "La computadora en {0} no es la que anunció la sala (el certificado no coincide). Pide la dirección a quien la aloja y entra por IP.");
    public static string JoinRoomFailedFormat => T(
        "Não foi possível entrar na sala em {0}. Confira o endereço, se vocês estão na mesma rede (LAN, Hamachi, Radmin ou Tailscale) e se o Firewall do Windows de quem hospeda liberou o ScreenCord.",
        "Couldn't join the room at {0}. Check the address, that you're on the same network (LAN, Hamachi, Radmin or Tailscale) and that the host's Windows Firewall allows ScreenCord.",
        "No se pudo entrar a la sala en {0}. Verifica la dirección, que estén en la misma red (LAN, Hamachi, Radmin o Tailscale) y que el Firewall de Windows de quien la aloja permita ScreenCord.");
    public static string RoomConnectionDropped => T("A conexão com a sala caiu.", "The connection to the room dropped.", "Se cayó la conexión con la sala.");
    public static string RoomClosed => T("A sala foi encerrada.", "The room was ended.", "La sala se finalizó.");
    public static string HostLeftRoomClosed => T("Quem hospedava saiu, e a sala foi encerrada.", "The host left, and the room was ended.", "Quien alojaba salió y la sala se finalizó.");
    public static string HostOtherVersion => T(
        "Quem hospeda a sala usa outra versão do ScreenCord. Atualizem o app.",
        "The room's host uses a different version of ScreenCord. Update the app.",
        "Quien aloja la sala usa otra versión de ScreenCord. Actualicen la aplicación.");
    public static string InvalidJoinRequest => T("Pedido de entrada inválido.", "Invalid join request.", "Solicitud de entrada no válida.");
    public static string RoomFullFormat => T("A sala está cheia ({0} pessoas).", "The room is full ({0} people).", "La sala está llena ({0} personas).");
    public static string AlreadyHosting => T("Você já está hospedando uma sala. Saia dela para criar outra.", "You're already hosting a room. Leave it to create another one.", "Ya estás alojando una sala. Sal de ella para crear otra.");
    public static string InvalidAddress => T(
        "Endereço inválido. Use o IP de quem hospeda, como 25.1.2.3 ou 192.168.0.10:47830.",
        "Invalid address. Use the host's IP, like 25.1.2.3 or 192.168.0.10:47830.",
        "Dirección no válida. Usa la IP de quien aloja, como 25.1.2.3 o 192.168.0.10:47830.");
    public static string OwnRoomAddress => T("Esse é o endereço da sala que você está hospedando.", "That's the address of the room you're hosting.", "Esa es la dirección de la sala que estás alojando.");
    public static string RoomNoLongerAvailable => T("A sala não está mais disponível.", "The room is no longer available.", "La sala ya no está disponible.");
    public static string RoomNoLongerOnNetwork => T("Essa sala não está mais na rede.", "That room is no longer on the network.", "Esa sala ya no está en la red.");
    public static string JoinLocalRoomToShare => T("Entre numa sala local para compartilhar.", "Join a local room to share.", "Entra a una sala local para compartir.");
    public static string NoLocalNetwork => T(
        "Este computador não está em nenhuma rede local (LAN, Hamachi, Radmin ou Tailscale): ninguém conseguiria assistir.",
        "This computer isn't on any local network (LAN, Hamachi, Radmin or Tailscale): nobody would be able to watch.",
        "Esta computadora no está en ninguna red local (LAN, Hamachi, Radmin o Tailscale): nadie podría mirar.");
    public static string ShareSettingsOutOfLimits => T("Configuração de compartilhamento fora dos limites das salas locais.", "Sharing settings are outside the limits of local rooms.", "La configuración de la transmisión está fuera de los límites de las salas locales.");
    public static string ShareNotAnnounced => T("O compartilhamento não foi anunciado na sala.", "The sharing wasn't announced in the room.", "La transmisión no se anunció en la sala.");
    public static string PortInUseFormat => T(
        "A porta {0} deste computador está em uso por outro programa, e o ScreenCord não consegue abrir a sala local.",
        "Port {0} on this computer is in use by another program, so ScreenCord can't open the local room.",
        "El puerto {0} de esta computadora está en uso por otro programa y ScreenCord no puede abrir la sala local.");
    public static string LocalOnlyWithoutAccount => T("Sem conta do Discord, só as salas locais estão disponíveis.", "Without a Discord account, only local rooms are available.", "Sin cuenta de Discord, solo están disponibles las salas locales.");
}
