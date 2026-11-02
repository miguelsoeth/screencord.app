namespace ScreenCord.Views;

/// <summary>Retângulo em unidades da interface, relativo ao canto do bloco das telas.</summary>
public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

/// <summary>
/// Cálculo da área das telas da janela principal, sem WPF (testável com qualquer tamanho de janela e de vídeo).
/// O bloco depende só do espaço disponível, nunca de quantas telas estão abertas: abrir ou fechar uma não muda o
/// tamanho do bloco (nem o cabeçalho e a lateral, que o acompanham).
/// </summary>
public static class StageLayout
{
    /// <summary>Proporção do bloco numa janela comum, e a das telas cujo tamanho ainda não se conhece.</summary>
    public const double DefaultAspect = 16.0 / 9;

    /// <summary>Abaixo desta proporção (janela alta, monitor vertical), o bloco cresce para baixo para caber telas empilhadas.</summary>
    public const double TallThreshold = 4.0 / 3;

    /// <summary>O bloco mais alto: 9:16, a proporção de um monitor vertical.</summary>
    public const double MinAspect = 9.0 / 16;

    /// <summary>Acima desta proporção (ultrawide), o bloco cresce para os lados para caber telas lado a lado.</summary>
    public const double WideThreshold = 2.0;

    /// <summary>O bloco mais largo: 21:9; numa janela ainda mais larga, ele fica centralizado.</summary>
    public const double MaxAspect = 21.0 / 9;

    /// <summary>Até quantas miniaturas ficam ao lado da transmissão fixada.</summary>
    public const int MaxThumbnails = 3;

    /// <summary>
    /// Tamanho do bloco no espaço disponível. Numa janela de proporção comum (de 4:3 a 2:1) é o maior 16:9 que
    /// cabe. Numa janela alta ele usa a altura toda (até 9:16) e numa ultrawide a largura toda (até 21:9), para as
    /// telas abertas juntas se empilharem ou ficarem lado a lado maiores. Arredondado para baixo, em unidades inteiras.
    /// </summary>
    public static (double Width, double Height) Stage(double availableWidth, double availableHeight)
    {
        if (!(availableWidth > 0) || !(availableHeight > 0) || double.IsInfinity(availableWidth) || double.IsInfinity(availableHeight))
        {
            return (0, 0);
        }

        var available = availableWidth / availableHeight;
        var aspect = available < TallThreshold ? Math.Max(available, MinAspect)
            : available > WideThreshold ? Math.Min(available, MaxAspect)
            : DefaultAspect;
        // Limitado pela altura, ela fica inteira; limitado pela largura, idem (sem perder um pixel no arredondamento).
        return availableWidth >= availableHeight * aspect
            ? (Math.Floor(availableHeight * aspect), Math.Floor(availableHeight))
            : (Math.Floor(availableWidth), Math.Floor(availableWidth / aspect));
    }

    /// <summary>
    /// Maior retângulo com a proporção <paramref name="aspect"/> (largura / altura) que cabe em <paramref name="cell"/>,
    /// centralizado nele. Proporção desconhecida ou inválida vale 16:9.
    /// </summary>
    public static LayoutRect Fit(LayoutRect cell, double aspect)
    {
        if (!(cell.Width > 0) || !(cell.Height > 0))
        {
            return new LayoutRect(cell.X, cell.Y, 0, 0);
        }

        aspect = Normalize(aspect);
        var width = Math.Min(cell.Width, cell.Height * aspect);
        var height = Math.Min(cell.Height, width / aspect);
        width = Math.Floor(width);
        height = Math.Floor(height);
        return new LayoutRect(
            Math.Floor(cell.X + (cell.Width - width) / 2),
            Math.Floor(cell.Y + (cell.Height - height) / 2),
            width,
            height);
    }

    /// <summary>
    /// Grade das telas abertas (na ordem de <paramref name="aspects"/>, a proporção de cada vídeo), com
    /// <paramref name="gap"/> entre elas. O número de colunas é o que deixa a soma das áreas dos vídeos maior: duas
    /// telas 16:9 ficam lado a lado numa janela larga e empilhadas num monitor vertical; duas telas em pé, lado a
    /// lado. Se sobra lugar na última linha, a última tela ocupa o resto. Cada tela fica centralizada na sua célula,
    /// no tamanho do vídeo.
    /// </summary>
    public static IReadOnlyList<LayoutRect> Grid(double width, double height, IReadOnlyList<double> aspects, double gap)
    {
        if (aspects.Count == 0 || !(width > 0) || !(height > 0))
        {
            return [.. aspects.Select(_ => new LayoutRect(0, 0, 0, 0))];
        }

        // Empate (até 1%, ex.: a linha entre as telas decide por um pixel): lado a lado num bloco largo, empilhadas num
        // alto. Por isso o bloco largo testa primeiro mais colunas, e o alto, menos; a seguinte só ganha se for melhor.
        var order = Enumerable.Range(1, aspects.Count);
        if (width >= height)
        {
            order = order.Reverse();
        }

        IReadOnlyList<LayoutRect> best = [];
        var bestArea = -1.0;
        foreach (var columns in order)
        {
            var cells = GridCells(width, height, aspects.Count, columns, gap);
            var tiles = cells.Select((cell, i) => Fit(cell, aspects[i])).ToList();
            var area = tiles.Sum(t => t.Area);
            if (area > bestArea * 1.01)
            {
                bestArea = area;
                best = tiles;
            }
        }

        return best;
    }

    /// <summary>
    /// A transmissão fixada ocupa a maior parte do bloco e as outras (até <see cref="MaxThumbnails"/>) ficam em
    /// miniatura numa faixa de um quarto do bloco: uma coluna à direita ou uma linha embaixo, o que deixar a fixada
    /// maior (embaixo num monitor vertical; à direita numa janela larga). Retorna a fixada e as miniaturas na ordem.
    /// </summary>
    public static (LayoutRect Main, IReadOnlyList<LayoutRect> Thumbnails) Pinned(
        double width, double height, double mainAspect, IReadOnlyList<double> others, double gap)
    {
        if (!(width > 0) || !(height > 0))
        {
            return (default, [.. others.Select(_ => default(LayoutRect))]);
        }

        if (others.Count == 0)
        {
            return (Fit(new LayoutRect(0, 0, width, height), mainAspect), []);
        }

        var count = Math.Min(others.Count, MaxThumbnails);
        var side = PinnedSide(width, height, mainAspect, others, count, gap);
        var bottom = PinnedBottom(width, height, mainAspect, others, count, gap);

        // Na dúvida (mesma área), a coluna à direita, como numa janela comum.
        return bottom.Main.Area > side.Main.Area * 1.001 ? bottom : side;
    }

    private static (LayoutRect Main, IReadOnlyList<LayoutRect> Thumbnails) PinnedSide(
        double width, double height, double mainAspect, IReadOnlyList<double> others, int count, double gap)
    {
        var strip = Math.Floor(width / 4);
        var main = Fit(new LayoutRect(0, 0, width - strip - gap, height), mainAspect);

        // Miniaturas na largura da faixa, empilhadas e centralizadas na altura; altas demais, encolhem para caber.
        var cellHeight = Math.Min(Math.Floor(strip / DefaultAspect), Math.Floor((height - gap * (count - 1)) / count));
        var top = Math.Floor((height - (cellHeight * count + gap * (count - 1))) / 2);
        var thumbnails = new List<LayoutRect>();
        for (var i = 0; i < others.Count; i++)
        {
            thumbnails.Add(i < count
                ? Fit(new LayoutRect(width - strip, top + i * (cellHeight + gap), strip, cellHeight), others[i])
                : default);
        }

        return (main, thumbnails);
    }

    private static (LayoutRect Main, IReadOnlyList<LayoutRect> Thumbnails) PinnedBottom(
        double width, double height, double mainAspect, IReadOnlyList<double> others, int count, double gap)
    {
        var strip = Math.Floor(height / 4);
        var main = Fit(new LayoutRect(0, 0, width, height - strip - gap), mainAspect);

        // Miniaturas lado a lado na altura da faixa, centralizadas na largura; largas demais, encolhem para caber.
        var cellWidth = Math.Min(Math.Floor(strip * DefaultAspect), Math.Floor((width - gap * (count - 1)) / count));
        var left = Math.Floor((width - (cellWidth * count + gap * (count - 1))) / 2);
        var thumbnails = new List<LayoutRect>();
        for (var i = 0; i < others.Count; i++)
        {
            thumbnails.Add(i < count
                ? Fit(new LayoutRect(left + i * (cellWidth + gap), height - strip, cellWidth, strip), others[i])
                : default);
        }

        return (main, thumbnails);
    }

    private static List<LayoutRect> GridCells(double width, double height, int count, int columns, double gap)
    {
        var rows = (count + columns - 1) / columns;
        var cellWidth = (width - gap * (columns - 1)) / columns;
        var cellHeight = (height - gap * (rows - 1)) / rows;
        var cells = new List<LayoutRect>(count);
        for (var index = 0; index < count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var span = index == count - 1 ? columns - column : 1;
            cells.Add(new LayoutRect(
                column * (cellWidth + gap),
                row * (cellHeight + gap),
                cellWidth * span + gap * (span - 1),
                cellHeight));
        }

        return cells;
    }

    /// <summary>Proporção usável: positiva e finita, entre 1:10 e 10:1; o resto (vídeo ainda sem tamanho) vale 16:9.</summary>
    public static double Normalize(double aspect) =>
        double.IsFinite(aspect) && aspect > 0 ? Math.Clamp(aspect, 0.1, 10) : DefaultAspect;
}
