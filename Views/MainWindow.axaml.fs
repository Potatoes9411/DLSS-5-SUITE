namespace DLSS_5_MANAGER.Views

open System
open System.Diagnostics
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Markup.Xaml
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Threading
open Avalonia.VisualTree
open DLSS_5_MANAGER.Services
open DLSS_5_MANAGER.ViewModels

type MainWindow() as this =
    inherit Window()

    // --- Card drag & drop state -------------------------------------------
    let mutable dragStartPos: Nullable<Point> = Nullable()
    let mutable draggedCard: GameCardViewModel option = None
    let mutable currentTargetCard: GameCardViewModel option = None
    let mutable isDraggingCard = false

    // --- Kinetic smooth-scroll state --------------------------------------
    // The wheel sets a target offset; a 60 fps timer eases the real offset
    // towards it, so the grid glides instead of jumping line by line.
    let mutable scrollTargetY = 0.0
    let mutable isScrollAnimating = false

    // Spring scrolling.
    //
    // The old model eased the offset toward the target by a fixed time
    // constant. That settles correctly but always the same way, so a flick and
    // a nudge felt identical and nothing ever carried momentum - which is what
    // "not flowy" was describing. A damped spring carries velocity between
    // frames instead: a fast wheel builds speed and coasts, a small one barely
    // moves, and both settle without a hard stop.
    let mutable scrollVelocity = 0.0

    // How far past an edge the content has been pushed, in pixels. A
    // ScrollViewer clamps its own Offset, so an overscroll cannot live there -
    // it is carried here and drawn as a transform on the content, then sprung
    // back to zero.
    let mutable overscroll = 0.0
    let mutable overscrollVelocity = 0.0
    let mutable overscrollTransform: TranslateTransform = null
    let mutable overscrollHost: Control = null
    /// Whichever page the wheel last touched - the games grid or the settings.
    let mutable activeScrollViewer: ScrollViewer = null

    /// Set once the constructor has built it; started by the wheel handler.
    let mutable scrollDriver: TimeSpan -> unit = fun _ -> ()
    // Driven by the render loop, not by a timer.
    //
    // A DispatcherTimer cannot match a display: at 8 ms it fired faster than
    // anything could be drawn and the extra ticks fought the frames that were,
    // and at 16 ms it produced sixty steps a second on a 180 Hz panel, which
    // is exactly the stutter that looks like 30 Hz. RequestAnimationFrame runs
    // once per frame the compositor actually presents, whatever rate that is,
    // so the glide has one step per frame on every machine.
    let scrollClock = Stopwatch.StartNew()
    let mutable lastScrollTick = 0.0

    // --- Starfield twinkle -------------------------------------------------
    // Driven from here rather than from a XAML animation. The styled version
    // measured as doing nothing on screen and there was no way to see why from
    // the outside; a timer is a few lines, is obvious when it runs, and can be
    // reasoned about. It also lets each plate follow its own sine rather than
    // a keyframe approximation of one.
    //
    // Three plates on periods that share no common factor, so their peaks
    // never line up and the brightening lands on different parts of the sky.
    let twinkleTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds(50.0))
    let twinkleClock = Stopwatch.StartNew()
    // ---- Viewport fade ---------------------------------------------------
    // Cards fade, shrink and settle as they cross the edges of the scroller
    // instead of appearing at full strength the instant they are realised.
    //
    // The grid virtualises, so only what is on screen (plus a little either
    // side) exists at all. These are those elements, tracked through the
    // repeater's own prepare/clear events - walking the visual tree per frame
    // to find them would cost far more than the effect is worth.
    //
    // Each card keeps one scale and one translate transform for its whole
    // realised life. Building transforms per frame would hand the compositor a
    // new object every time and throw away its cached matrix.
    let realizedCards = ResizeArray<Control * ScaleTransform * TranslateTransform>()
    let mutable gamesScroller: ScrollViewer = null
    let mutable gamesRepeater: ItemsRepeater = null
    let mutable cardFadeDriver: unit -> unit = fun () -> ()

    // Parallax. The starfield drifts against the grid as it scrolls, which is
    // what gives the page a sense of depth rather than of one flat sheet
    // sliding. Each plate moves at its own rate - the nearest fastest - so they
    // separate from one another as well as from the content.
    let mutable plateParallax: TranslateTransform array = [||]

    // ---- Drag spring -----------------------------------------------------
    // The ghost used to be written straight to the cursor position. That
    // tracks perfectly and feels dead - there is no weight to it. It now
    // chases the cursor on a spring and tilts into the direction it is
    // travelling, which is what gives a dragged card the sense of having mass.
    let mutable ghostX = 0.0
    let mutable ghostY = 0.0
    let mutable ghostVX = 0.0
    let mutable ghostVY = 0.0
    let mutable ghostTargetX = 0.0
    let mutable ghostTargetY = 0.0
    let mutable isGhostAnimating = false
    let mutable ghostDriver: TimeSpan -> unit = fun _ -> ()
    let mutable ghostTilt: RotateTransform = null
    let mutable ghostScale: ScaleTransform = null
    let ghostClock = Stopwatch.StartNew()
    let mutable lastGhostTick = 0.0

    // Card bounds, captured once when a drag begins.
    //
    // The hit test used to walk the grid's visual tree on every qualifying
    // pointer move. Moving the pointer quickly produces a great many of those,
    // and each walk allocates an enumerator over every descendant - which is
    // exactly the stutter that showed up when a card was dragged fast. The
    // cards cannot move during a drag, so their rectangles are worth measuring
    // once and reusing.
    let mutable dragRects: (GameCardViewModel * Rect) array = [||]

    // Resolved once when the window opens. Both were being looked up on
    // every single pointer-move during a drag, which is a visual-tree walk per
    // mouse event.
    let mutable dragGhost: Border = null
    let mutable gamesHost: Control = null
    let mutable lastHitTestPos = Point(-9999.0, -9999.0)

    let mutable starPlates: Control array = [||]

    /// period in seconds, phase offset in radians
    let twinklePlan = [| 3.1, 0.0; 4.3, 2.1; 5.9, 4.2 |]

    // --- Shooting stars ----------------------------------------------------
    // Rare on purpose. One every twelve to forty seconds, at a random place,
    // on a random heading, and never two at once from the same slot - often
    // enough to be caught out of the corner of an eye, seldom enough that it
    // stays an event rather than weather.
    let shooterRng = Random()
    let mutable shooters: Border array = [||]

    /// Per streak: elapsed, duration, start, direction, and when it next runs.
    let mutable shooterState:
        (float * float * float * float * float * float * float) array = [||]

    /// Built once per streak and mutated in place. Allocating a TransformGroup
    /// and two transforms per streak per tick was twenty rounds of garbage a
    /// second for something whose only job is to hold two numbers.
    let mutable shooterRotate: RotateTransform array = [||]
    let mutable shooterTranslate: TranslateTransform array = [||]

    let scheduleShooter (now: float) =
        now + 12.0 + shooterRng.NextDouble() * 28.0

    do
        this.InitializeComponent()

        // Find the star plates once, then drive their opacity every frame.
        this.Opened.Add(fun _ ->
            starPlates <-
                [| "StarPlate1"; "StarPlate2"; "StarPlate3" |]
                |> Array.map (fun name -> this.FindControl<Control>(name))
                |> Array.filter (fun c -> not (isNull (box c)))

            dragGhost <- this.FindControl<Border>("FloatingDragGhost")
            gamesHost <- this.FindControl<Control>("GamesItemsControl")
            gamesScroller <- this.FindControl<ScrollViewer>("GamesScrollViewer")
            gamesRepeater <- this.FindControl<ItemsRepeater>("GamesItemsControl")

            if not (isNull (box gamesRepeater)) then
                // Give every card its transforms as it is realised, and take it
                // off the list again when the repeater recycles it.
                gamesRepeater.ElementPrepared.Add(fun e ->
                    match e.Element with
                    | element when not (isNull element) ->
                        let scale = ScaleTransform(0.94, 0.94)
                        let slide = TranslateTransform(0.0, 14.0)
                        let group = TransformGroup()
                        group.Children.Add(scale)
                        group.Children.Add(slide)
                        element.RenderTransformOrigin <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
                        element.RenderTransform <- group
                        realizedCards.Add((element, scale, slide))
                        cardFadeDriver ()
                    | _ -> ())

                gamesRepeater.ElementClearing.Add(fun e ->
                    match e.Element with
                    | element when not (isNull element) ->
                        let mutable i = realizedCards.Count - 1

                        while i >= 0 do
                            let (c, _, _) = realizedCards.[i]
                            if Object.ReferenceEquals(c, element) then realizedCards.RemoveAt(i)
                            i <- i - 1

                        // Hand it back unfaded: the repeater reuses containers,
                        // and a recycled card that kept a stale opacity would
                        // reappear half transparent somewhere else.
                        element.Opacity <- 1.0
                    | _ -> ())

            if not (isNull (box gamesScroller)) then
                // Covers every other way the view can move - the scrollbar,
                // the keyboard, a resize - not just the wheel spring.
                gamesScroller.ScrollChanged.Add(fun _ -> cardFadeDriver ())
                gamesScroller.SizeChanged.Add(fun _ -> cardFadeDriver ())

            shooters <-
                [| "Shooter1"; "Shooter2"; "Shooter3" |]
                |> Array.map (fun name -> this.FindControl<Border>(name))
                |> Array.filter (fun c -> not (isNull (box c)))

            // One transform chain per streak, reused for its whole life.
            shooterRotate <- shooters |> Array.map (fun _ -> RotateTransform(0.0))
            shooterTranslate <- shooters |> Array.map (fun _ -> TranslateTransform(0.0, 0.0))

            shooters
            |> Array.iteri (fun i s ->
                let group = TransformGroup()
                group.Children.Add(shooterRotate.[i])
                group.Children.Add(shooterTranslate.[i])
                s.RenderTransform <- group)

            // Staggered first runs, so they do not all arrive together.
            shooterState <-
                shooters
                |> Array.mapi (fun i _ ->
                    (0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 4.0 + float i * 9.0 + shooterRng.NextDouble() * 10.0))

            // Each plate gets a transform that both scales it up slightly and
            // carries its parallax offset. The scale matters: the plates are
            // UniformToFill, and without a little overscan a parallax shift
            // could pull an edge into view.
            plateParallax <-
                starPlates
                |> Array.map (fun plate ->
                    let slide = TranslateTransform(0.0, 0.0)
                    let scale = ScaleTransform(1.06, 1.06)
                    let group = TransformGroup()
                    group.Children.Add(scale)
                    group.Children.Add(slide)
                    plate.RenderTransformOrigin <- RelativePoint(0.5, 0.5, RelativeUnit.Relative)
                    plate.RenderTransform <- group
                    slide)

            if starPlates.Length > 0 || shooters.Length > 0 then twinkleTimer.Start())

        twinkleTimer.Tick.Add(fun _ ->
            // Parked with the rest of the background motion: nothing should be
            // redrawing while the window is not the one being looked at.
            let motionOn =
                match this.DataContext with
                | :? MainViewModel as vm -> vm.IsBackgroundMotionOn
                | _ -> true

            if motionOn then
                let t = twinkleClock.Elapsed.TotalSeconds

                for i in 0 .. starPlates.Length - 1 do
                    if i < twinklePlan.Length then
                        let (period, phase) = twinklePlan.[i]
                        let wave = sin (2.0 * Math.PI * t / period + phase)
                        // 0.16 .. 1.00. The plates carry their own glow now, so
                        // the top of the swing is worth taking all the way up -
                        // that is what makes a star read as flaring rather than
                        // just getting slightly less grey.
                        starPlates.[i].Opacity <- 0.58 + 0.42 * wave

                // Shooting stars.
                let w = max 400.0 this.Bounds.Width
                let h = max 300.0 this.Bounds.Height

                for i in 0 .. shooters.Length - 1 do
                    let (elapsed, duration, x0, y0, dx, dy, nextAt) = shooterState.[i]

                    if duration <= 0.0 then
                        // Idle: start one when its turn comes round.
                        if t >= nextAt then
                            // Anywhere in the upper two thirds, heading down
                            // and to one side or the other.
                            let sx = shooterRng.NextDouble() * w
                            let sy = shooterRng.NextDouble() * h * 0.6
                            let angle =
                                if shooterRng.Next(2) = 0 then 0.35 + shooterRng.NextDouble() * 0.5
                                else Math.PI - (0.35 + shooterRng.NextDouble() * 0.5)

                            let travel = 260.0 + shooterRng.NextDouble() * 420.0
                            let dur = 0.55 + shooterRng.NextDouble() * 0.5

                            let s = shooters.[i]
                            s.RenderTransformOrigin <- RelativePoint(1.0, 0.5, RelativeUnit.Relative)
                            s.Width <- 110.0 + shooterRng.NextDouble() * 90.0

                            shooterState.[i] <-
                                (0.0, dur, sx, sy,
                                 cos angle * travel, sin angle * travel, nextAt)
                    else
                        let e = elapsed + 0.05
                        let p = e / duration

                        if p >= 1.0 then
                            shooters.[i].Opacity <- 0.0
                            shooterState.[i] <- (0.0, 0.0, 0.0, 0.0, 0.0, 0.0, scheduleShooter t)
                        else
                            let s = shooters.[i]

                            shooterRotate.[i].Angle <- atan2 dy dx * 180.0 / Math.PI
                            shooterTranslate.[i].X <- x0 + dx * p
                            shooterTranslate.[i].Y <- y0 + dy * p
                            // In fast, out slow, so the head leads and the
                            // streak dies away rather than blinking off.
                            s.Opacity <- sin (Math.PI * (p ** 0.75))
                            shooterState.[i] <- (e, duration, x0, y0, dx, dy, nextAt))

        // Clear TextBox focus whenever the user clicks anywhere outside of it
        this.AddHandler(
            InputElement.PointerPressedEvent,
            EventHandler<PointerPressedEventArgs>(fun _ e ->
                try
                    let topLevel = TopLevel.GetTopLevel(this)
                    if topLevel <> null && topLevel.FocusManager <> null then
                        let focused = topLevel.FocusManager.GetFocusedElement()
                        if not (isNull (box focused)) && focused :? TextBox then
                            let isClickInsideTextBox =
                                match e.Source with
                                | :? Visual as v ->
                                    let rec isDescendantOfTextBox (cur: Visual) =
                                        if isNull (box cur) then false
                                        elif cur :? TextBox then true
                                        else isDescendantOfTextBox (cur.GetVisualParent())

                                    isDescendantOfTextBox v
                                | _ -> false

                            if not isClickInsideTextBox then
                                let sink = this.FindControl<Control>("DummyFocusSink")
                                if sink <> null then sink.Focus() |> ignore else this.Focus() |> ignore
                with _ -> ()),
            RoutingStrategies.Tunnel
        )

        // Park the background animations whenever the window is not the one in
        // front. They are the app's largest continuous GPU cost and nobody is
        // watching them from behind another window.
        let setMotion (on: bool) =
            match this.DataContext with
            | :? MainViewModel as vm -> vm.IsWindowActive <- on
            | _ -> ()

        this.Activated.Add(fun _ -> setMotion true)
        this.Deactivated.Add(fun _ -> setMotion false)

        // One step per presented frame, easing by elapsed time rather than by
        // a fixed fraction - so the glide takes the same 90 ms to settle on a
        // 60 Hz panel and on a 180 Hz one, and simply looks smoother on the
        // faster one because it is drawn more often.
        let rec scrollStep (_: TimeSpan) =
            let sv = activeScrollViewer

            if isNull (box sv) then
                isScrollAnimating <- false
            else
                let now = scrollClock.Elapsed.TotalSeconds
                let dt = Math.Clamp(now - lastScrollTick, 0.0005, 0.05)
                lastScrollTick <- now

                let current = sv.Offset.Y
                let maxOffset = max 0.0 (sv.Extent.Height - sv.Viewport.Height)

                // ---- the scroll spring -------------------------------------
                // Critically-ish damped: stiffness sets how hard it pulls to
                // the target, damping how quickly the velocity bleeds off.
                // Damping just under 2*sqrt(stiffness) leaves a trace of
                // follow-through without visible bounce at the destination.
                let stiffness = 210.0
                let damping = 26.0

                let diff = scrollTargetY - current
                let accel = stiffness * diff - damping * scrollVelocity
                scrollVelocity <- scrollVelocity + accel * dt

                let stepped = current + scrollVelocity * dt

                // The target is always a real offset, so this only ever trims
                // the last fraction of a pixel. The band is fed by the wheel
                // handler, not from here.
                //
                // It used to be fed here, and that was the bug: the wheel was
                // allowed to park the target past the end, so every frame the
                // spring drove the position beyond the edge, which topped the
                // band back up and damped the velocity again. The band never
                // emptied and scrolling away from the edge could not win
                // against it.
                let clamped = Math.Clamp(stepped, 0.0, maxOffset)

                // Arriving at an edge with speed left over throws that speed
                // into the band instead of through the wall.
                if stepped < -0.01 && scrollVelocity < 0.0 then
                    overscroll <- Math.Clamp(overscroll - scrollVelocity * 0.05, -170.0, 170.0)
                    scrollVelocity <- 0.0
                elif stepped > maxOffset + 0.01 && scrollVelocity > 0.0 then
                    overscroll <- Math.Clamp(overscroll - scrollVelocity * 0.05, -170.0, 170.0)
                    scrollVelocity <- 0.0

                sv.Offset <- Vector(sv.Offset.X, clamped)
                cardFadeDriver ()

                // ---- the overscroll spring ---------------------------------
                if abs overscroll > 0.01 || abs overscrollVelocity > 0.01 then
                    let obounce = 260.0
                    let odamp = 24.0
                    let oaccel = -obounce * overscroll - odamp * overscrollVelocity
                    overscrollVelocity <- overscrollVelocity + oaccel * dt
                    overscroll <- overscroll + overscrollVelocity * dt

                    if abs overscroll < 0.15 && abs overscrollVelocity < 2.0 then
                        overscroll <- 0.0
                        overscrollVelocity <- 0.0

                if not (isNull (box overscrollTransform)) then
                    // Signed the way the content travels: positive pulls it
                    // down (past the top), negative lifts it (past the bottom).
                    overscrollTransform.Y <- overscroll

                // ---- settle ------------------------------------------------
                let atRest =
                    abs (scrollTargetY - clamped) < 0.4
                    && abs scrollVelocity < 6.0
                    && abs overscroll < 0.15
                    && abs overscrollVelocity < 2.0

                if atRest then
                    sv.Offset <- Vector(sv.Offset.X, Math.Clamp(scrollTargetY, 0.0, maxOffset))
                    scrollVelocity <- 0.0
                    overscroll <- 0.0
                    overscrollVelocity <- 0.0
                    if not (isNull (box overscrollTransform)) then overscrollTransform.Y <- 0.0
                    isScrollAnimating <- false
                else
                    this.RequestAnimationFrame(scrollStep)

        scrollDriver <- scrollStep

        // Smooth Hermite ramp: no hard edge where the fade begins or ends,
        // which a linear ramp always shows as a visible seam.
        let smoothstep (t: float) =
            let x = Math.Clamp(t, 0.0, 1.0)
            x * x * (3.0 - 2.0 * x)

        let updateCardFade () =
            if not (isNull (box gamesScroller)) && realizedCards.Count > 0 then
                let vh = gamesScroller.Viewport.Height

                if vh > 1.0 then
                    // The band is a fraction of the viewport rather than a
                    // fixed pixel count, so it follows the window size, the
                    // display scaling and how many rows actually fit. A small
                    // window fades over a shorter distance; a large one has
                    // room for a longer, softer ramp.
                    let band = Math.Clamp(vh * 0.22, 70.0, 260.0)

                    for i in 0 .. realizedCards.Count - 1 do
                        let (card, scale, slide) = realizedCards.[i]

                        if not (isNull (box card)) then
                            let p = card.TranslatePoint(Point(0.0, 0.0), gamesScroller)

                            if p.HasValue then
                                let top = p.Value.Y
                                let h = if card.Bounds.Height > 1.0 then card.Bounds.Height else 320.0
                                let bottom = top + h

                                // How far the card is from whichever edge it is
                                // nearest, in units of the fade band. Fully
                                // inside gives 1, fully outside gives 0.
                                let nearest = min bottom (vh - top)
                                let f = smoothstep (nearest / band)

                                // Reaches full well before the scale does, so
                                // the overshoot happens on a card you can
                                // already see.
                                card.Opacity <- Math.Clamp(0.06 + 1.45 * f, 0.0, 1.0)

                                // The shrink and drop are small on purpose: the
                                // point is depth, not a card that visibly
                                // launches itself into place.
                                // Velocity squash. Moving fast, the cards
                                // compress very slightly along the direction of
                                // travel and stretch across it - the same trick
                                // a flipbook uses to sell speed. Capped low
                                // enough to be felt rather than seen.
                                let squash = Math.Clamp(abs scrollVelocity / 5200.0, 0.0, 0.035)

                                // Overshoot on the way in. A straight ramp from
                                // 0.94 to 1.0 arrives without ever announcing
                                // that it did; f^2*(1-f) adds a bump that peaks
                                // around two thirds of the way through and is
                                // exactly zero at both ends, so the card swells
                                // past full size and settles back to it rather
                                // than creeping up to it.
                                //
                                // Position-based, not time-based: it is driven
                                // by where the card is in the viewport, so
                                // scrolling back up plays it in reverse instead
                                // of firing a fresh animation.
                                let overshoot = 0.40 * f * f * (1.0 - f)
                                let k = 0.94 + 0.06 * f + overshoot

                                scale.ScaleX <- k * (1.0 + squash)
                                scale.ScaleY <- k * (1.0 - squash)
                                slide.Y <- (1.0 - f) * 14.0

                    // The sky moves against the grid, a fraction of its speed.
                    if plateParallax.Length > 0 then
                        let offset = gamesScroller.Offset.Y

                        for i in 0 .. plateParallax.Length - 1 do
                            // 3%, 5%, 7% - near plates travel further.
                            let rate = 0.03 + 0.02 * float i
                            plateParallax.[i].Y <- -offset * rate

        cardFadeDriver <- updateCardFade

        // The ghost's own spring, on the same frame callback as the scroller.
        let rec ghostStep (_: TimeSpan) =
            if not isDraggingCard || isNull (box dragGhost) then
                isGhostAnimating <- false
            else
                let now = ghostClock.Elapsed.TotalSeconds
                let dt = Math.Clamp(now - lastGhostTick, 0.0005, 0.05)
                lastGhostTick <- now

                // Stiff enough to stay under the cursor, loose enough to lag a
                // few pixels when the pointer whips across the grid.
                let stiffness = 340.0
                let damping = 30.0

                ghostVX <- ghostVX + (stiffness * (ghostTargetX - ghostX) - damping * ghostVX) * dt
                ghostVY <- ghostVY + (stiffness * (ghostTargetY - ghostY) - damping * ghostVY) * dt
                ghostX <- ghostX + ghostVX * dt
                ghostY <- ghostY + ghostVY * dt

                Canvas.SetLeft(dragGhost, ghostX)
                Canvas.SetTop(dragGhost, ghostY)

                // Tilt follows horizontal speed, capped so a fast flick leans
                // rather than spins.
                if not (isNull (box ghostTilt)) then
                    ghostTilt.Angle <- Math.Clamp(ghostVX * 0.018, -11.0, 11.0)

                this.RequestAnimationFrame(ghostStep)

        ghostDriver <- ghostStep

    member private this.InitializeComponent() = AvaloniaXamlLoader.Load(this)

    // =====================================================================
    // WINDOW CHROME
    // =====================================================================
    member this.OnHeaderPointerPressed(sender: obj, e: PointerPressedEventArgs) =
        if e.GetCurrentPoint(this).Properties.IsLeftButtonPressed then this.BeginMoveDrag(e)

    member this.OnMinimizeClicked(sender: obj, e: RoutedEventArgs) =
        this.WindowState <- WindowState.Minimized

    member this.OnMaximizeClicked(sender: obj, e: RoutedEventArgs) =
        if this.WindowState = WindowState.Maximized then
            this.WindowState <- WindowState.Normal
        else
            this.WindowState <- WindowState.Maximized

    member this.OnCloseClicked(sender: obj, e: RoutedEventArgs) = this.Close()

    // =====================================================================
    // KINETIC SMOOTH SCROLLING
    // =====================================================================
    member this.OnGamesWheelChanged(sender: obj, e: PointerWheelEventArgs) =
        this.SmoothScroll(this.FindControl<ScrollViewer>("GamesScrollViewer"), e)

    /// The settings page grew past a single screen, so it gets the same kinetic
    /// wheel handling as the grid instead of the default line-by-line stepping.
    member this.OnSettingsWheelChanged(sender: obj, e: PointerWheelEventArgs) =
        this.SmoothScroll(this.FindControl<ScrollViewer>("SettingsScrollViewer"), e)

    member private this.SmoothScroll(sv: ScrollViewer, e: PointerWheelEventArgs) =
        // Switching between the two pages restarts the easing on the new one.
        if not (Object.ReferenceEquals(sv, activeScrollViewer)) then
            // Leaving a view: put whatever it was holding back where it was.
            if not (isNull (box overscrollTransform)) then overscrollTransform.Y <- 0.0

            activeScrollViewer <- sv
            isScrollAnimating <- false
            scrollVelocity <- 0.0
            overscroll <- 0.0
            overscrollVelocity <- 0.0

            // The band moves the scrolled content, not the viewport, so the
            // transform goes on whatever this ScrollViewer is presenting. It is
            // reused for the life of that content - building one per frame
            // would throw away the compositor's cached transform every time.
            overscrollTransform <- null
            overscrollHost <- null

            if not (isNull (box sv)) then
                match sv.Content with
                | :? Control as content ->
                    overscrollHost <- content

                    match content.RenderTransform with
                    | :? TranslateTransform as existing -> overscrollTransform <- existing
                    | _ ->
                        let t = TranslateTransform(0.0, 0.0)
                        content.RenderTransform <- t
                        overscrollTransform <- t
                | _ -> ()

        if not (isNull (box sv)) then
            let maxOffset = max 0.0 (sv.Extent.Height - sv.Viewport.Height)
            if maxOffset > 0.0 then
                // Re-sync the target whenever a new gesture starts.
                if not isScrollAnimating then
                    scrollTargetY <- sv.Offset.Y
                    scrollVelocity <- 0.0

                let step = 145.0

                // Notches that arrive while the spring is still moving stack
                // up, so a fast flick travels much further than the same number
                // of slow ones - that is the momentum the old model threw away.
                let proposed = scrollTargetY - (e.Delta.Y * step)

                // The target itself never leaves the real range. Anything that
                // would go past an end is handed to the band instead, against a
                // resistance that grows with how far it has already been
                // pulled - so it firms up rather than stretching without limit.
                //
                // Scrolling back toward the content always reduces the band
                // first, which is what makes an edge escapable: the wheel is
                // not fighting a target that is parked outside the range.
                if proposed < 0.0 then
                    let beyond = -proposed
                    let resisted = beyond / (1.0 + abs overscroll / 55.0)
                    overscroll <- Math.Clamp(overscroll + resisted * 0.32, -170.0, 170.0)
                    scrollTargetY <- 0.0
                elif proposed > maxOffset then
                    let beyond = proposed - maxOffset
                    let resisted = beyond / (1.0 + abs overscroll / 55.0)
                    overscroll <- Math.Clamp(overscroll - resisted * 0.32, -170.0, 170.0)
                    scrollTargetY <- maxOffset
                else
                    scrollTargetY <- proposed

                if not isScrollAnimating then
                    isScrollAnimating <- true
                    lastScrollTick <- scrollClock.Elapsed.TotalSeconds
                    this.RequestAnimationFrame(scrollDriver)

                e.Handled <- true

    // =====================================================================
    // CARD DRAG & DROP REORDERING
    // =====================================================================
    member private this.PerformDropSwap(vm: MainViewModel) =
        // A press that never turned into a drag is a plain click: open the sheet.
        let clickedCard =
            if not isDraggingCard && dragStartPos.HasValue then draggedCard else None

        if isDraggingCard && draggedCard.IsSome then
            match currentTargetCard with
            | Some target when not (Object.ReferenceEquals(target, draggedCard.Value)) ->
                target.IsDragTarget <- false
                vm.SwapCards(draggedCard.Value, target)
            | _ -> ()

            draggedCard.Value.IsDragging <- false
        elif draggedCard.IsSome then
            draggedCard.Value.IsDragging <- false

        match clickedCard with
        | Some card when not vm.IsManageOpen -> vm.OpenManage(card)
        | _ -> ()

        match currentTargetCard with
        | Some t -> t.IsDragTarget <- false
        | None -> ()

        vm.IsDraggingCard <- false
        vm.DraggedCard <- None
        isDraggingCard <- false
        draggedCard <- None
        currentTargetCard <- None
        dragStartPos <- Nullable()

        // Let the ghost's frame callback stop, drop the measured rectangles,
        // and level the tilt so the next drag does not start leaning.
        isGhostAnimating <- false
        dragRects <- [||]
        ghostVX <- 0.0
        ghostVY <- 0.0
        if not (isNull (box ghostTilt)) then ghostTilt.Angle <- 0.0

    member this.OnCardPointerPressed(sender: obj, e: PointerPressedEventArgs) =
        let point = e.GetCurrentPoint(this)
        if point.Properties.IsLeftButtonPressed then
            match sender with
            | :? Control as ctrl ->
                match ctrl.DataContext with
                | :? GameCardViewModel as card ->
                    dragStartPos <- Nullable(e.GetPosition(this))
                    draggedCard <- Some card
                    currentTargetCard <- None
                    isDraggingCard <- false
                | _ -> ()
            | _ -> ()

    member this.OnCardPointerMoved(sender: obj, e: PointerEventArgs) =
        if dragStartPos.HasValue && draggedCard.IsSome then
            let currentPos = e.GetPosition(this)
            let deltaX = currentPos.X - dragStartPos.Value.X
            let deltaY = currentPos.Y - dragStartPos.Value.Y
            let dist = Math.Sqrt(deltaX * deltaX + deltaY * deltaY)

            match this.DataContext with
            | :? MainViewModel as vm ->
                if dist > 8.0 && not isDraggingCard then
                    isDraggingCard <- true
                    draggedCard.Value.IsDragging <- true
                    vm.DraggedCard <- draggedCard
                    vm.IsDraggingCard <- true

                    // Measure every card once, now, rather than per move.
                    dragRects <-
                        if isNull (box gamesHost) then
                            [||]
                        else
                            gamesHost.GetVisualDescendants()
                            |> Seq.choose (fun d ->
                                match d with
                                | :? StackPanel as sp when sp.Classes.Contains("GameCardItem") && sp.IsVisible ->
                                    match sp.DataContext, sp.TranslatePoint(Point(0.0, 0.0), this) with
                                    | (:? GameCardViewModel as cardVm), p when p.HasValue ->
                                        let w = if sp.Bounds.Width > 10.0 then sp.Bounds.Width else 216.0
                                        let h = if sp.Bounds.Height > 10.0 then sp.Bounds.Height else 320.0
                                        Some(cardVm, Rect(p.Value.X, p.Value.Y, w, h))
                                    | _ -> None
                                | _ -> None)
                            |> Seq.toArray

                    // Start the ghost under the cursor so it does not fly in
                    // from wherever it was left last time.
                    ghostX <- currentPos.X - 108.0
                    ghostY <- currentPos.Y - 143.0
                    ghostVX <- 0.0
                    ghostVY <- 0.0

                    if not (isNull (box dragGhost)) then
                        if isNull (box ghostTilt) then
                            let tilt = RotateTransform(0.0)
                            let scale = ScaleTransform(1.0, 1.0)
                            let group = TransformGroup()
                            group.Children.Add(scale)
                            group.Children.Add(tilt)
                            dragGhost.RenderTransform <- group
                            ghostTilt <- tilt
                            ghostScale <- scale

                        Canvas.SetLeft(dragGhost, ghostX)
                        Canvas.SetTop(dragGhost, ghostY)

                    if not isGhostAnimating then
                        isGhostAnimating <- true
                        lastGhostTick <- ghostClock.Elapsed.TotalSeconds
                        this.RequestAnimationFrame(ghostDriver)

                if isDraggingCard then
                    // 1. Hand the spring a new target. The frame callback
                    //    moves the ghost; writing it here as well would fight
                    //    the spring and cancel the follow-through.
                    ghostTargetX <- currentPos.X - 108.0
                    ghostTargetY <- currentPos.Y - 143.0

                    // 2. Bounding-box hit test against the realised cards.
                    //
                    //    Two things keep this off the critical path. It only
                    //    runs once the pointer has actually moved a few pixels -
                    //    a mouse reports far more moves than the grid can change
                    //    under - and it stops at the first card containing the
                    //    point instead of walking the whole subtree every time.
                    //    Before, every mouse event walked all visual descendants
                    //    of the grid and kept going after it had its answer,
                    //    which is what made dragging stutter.
                    let moved =
                        let dx = currentPos.X - lastHitTestPos.X
                        let dy = currentPos.Y - lastHitTestPos.Y
                        dx * dx + dy * dy > 16.0

                    if moved then
                        lastHitTestPos <- currentPos

                        // A loop over an array of rectangles, which is what a
                        // hit test should cost.
                        let mutable found: GameCardViewModel option = None
                        let mutable i = 0

                        while found.IsNone && i < dragRects.Length do
                            let (cardVm, rect) = dragRects.[i]

                            if rect.Contains(currentPos)
                               && not (Object.ReferenceEquals(cardVm, draggedCard.Value)) then
                                found <- Some cardVm

                            i <- i + 1

                        let newTarget = found

                        // 3. Move the highlight when the hovered target changes
                        if currentTargetCard <> newTarget then
                            match currentTargetCard with
                            | Some prevTarget -> prevTarget.IsDragTarget <- false
                            | None -> ()

                            currentTargetCard <- newTarget

                            match currentTargetCard with
                            | Some target -> target.IsDragTarget <- true
                            | None -> ()
            | _ -> ()

    member this.OnCardPointerReleased(sender: obj, e: PointerReleasedEventArgs) =
        e.Pointer.Capture(null)
        match this.DataContext with
        | :? MainViewModel as vm ->
            this.PerformDropSwap(vm)
            e.Handled <- true
        | _ -> ()

    member this.OnCardPointerCaptureLost(sender: obj, e: PointerCaptureLostEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.PerformDropSwap(vm)
        | _ -> ()

    // =====================================================================
    // NAVIGATION & LIBRARY ACTIONS
    // =====================================================================
    member this.OnTabGamesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowGames()
        | _ -> ()

    member this.OnTabEmulatorsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowEmulators()
        | _ -> ()

    // =====================================================================
    // COMMUNITY
    // =====================================================================
    member this.OnTabCommunityClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShowCommunity()
        | _ -> ()

    member this.OnCommunityTutorialsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.Community.TutorialsUrl)
        | _ -> ()

    member this.OnCommunityRefreshClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.Refresh()
        | _ -> ()

    /// The filter chips carry their value in Tag, so one handler serves the
    /// whole row and adding a route later is a line of XAML.
    member this.OnCommunityRouteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            vm.Community.SetRouteFilter(if isNull ctrl.Tag then "" else string ctrl.Tag)
        | _ -> ()

    member this.OnCommunityResultClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            vm.Community.SetResultFilter(if isNull ctrl.Tag then "" else string ctrl.Tag)
        | _ -> ()

    member this.OnCommunityClaimNameClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.ClaimName()
        | _ -> ()

    member this.OnCommunityGameClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? CommunityGameViewModel as game -> vm.Community.OpenGame(game)
            | _ -> ()
        | _ -> ()

    member this.OnCommunitySheetCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.CloseSheet()
        | _ -> ()

    member this.OnCommunitySheetRouteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            vm.Community.SetSheetRoute(if isNull ctrl.Tag then "" else string ctrl.Tag)
        | _ -> ()

    member this.OnCommunityCommentsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? CommunityReportViewModel as report -> vm.Community.ToggleComments(report)
            | _ -> ()
        | _ -> ()

    member this.OnCommunityReplyClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? CommunityReportViewModel as report -> vm.Community.SendReply(report)
            | _ -> ()
        | _ -> ()

    /// The five emoji buttons share a handler; Tag holds the slot number the
    /// server stores the count in.
    member this.OnCommunityReactClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext, Int32.TryParse(string ctrl.Tag) with
            | (:? CommunityReportViewModel as report), (true, slot) -> vm.Community.React(report, slot)
            | _ -> ()
        | _ -> ()

    // ---- the composer ---------------------------------------------------
    member this.OnShareToCommunityClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ShareToCommunity()
        | _ -> ()

    member this.OnComposerCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.CloseComposer()
        | _ -> ()

    member this.OnComposerStatusClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            vm.Community.SetComposeStatus(if isNull ctrl.Tag then "working" else string ctrl.Tag)
        | _ -> ()

    member this.OnComposerDetectSpecsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.DetectSpecs()
        | _ -> ()

    member this.OnComposerClearSpecsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.ClearSpecs()
        | _ -> ()

    member this.OnComposerPostClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.Community.SubmitReport()
        | _ -> ()

    /// Emulator cards do not drag or reorder, so a plain release opens them.
    member this.OnEmulatorCardPressed(sender: obj, e: PointerReleasedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) when e.InitialPressMouseButton = MouseButton.Left ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card when not vm.IsManageOpen -> vm.OpenManage(card)
            | _ -> ()
        | _ -> ()

    member this.OnRemoveEmulatorClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card -> vm.RemoveEmulator(card)
            | _ -> ()
        | _ -> ()

    member this.OnAddEmulatorClicked(sender: obj, e: RoutedEventArgs) =
        let options = FilePickerOpenOptions()
        options.Title <- "Select the emulator executable (.exe)"
        options.AllowMultiple <- false
        let fileType = FilePickerFileType("Executable Files (*.exe)")
        fileType.Patterns <- [| "*.exe" |]
        options.FileTypeFilter <- [| fileType |]

        async {
            let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask

            if files <> null && files.Count > 0 then
                match this.DataContext with
                | :? MainViewModel as vm ->
                    vm.ShowEmulators()
                    vm.AddEmulatorExecutable(files.[0].Path.LocalPath)
                | _ -> ()
        }
        |> Async.StartImmediate

    member this.OnDetectEmulatorsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.ShowEmulators()
            vm.DetectEmulators()
        | _ -> ()

    member this.OnEmulatorsWheelChanged(sender: obj, e: PointerWheelEventArgs) =
        this.SmoothScroll(this.FindControl<ScrollViewer>("EmulatorsScrollViewer"), e)

    member this.OnSettingsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ToggleSettings()
        | _ -> ()

    member this.OnCheckLosslessScalingClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.CheckLosslessScaling()
        | _ -> ()

    member this.OnBackToGamesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.CloseSettings()
        | _ -> ()

    member this.OnSearchClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.ToggleSearch()
            if vm.IsSearchOpen then
                let searchBox =
                    let sb1 = this.FindControl<TextBox>("SearchInputBox")
                    let sb2 = this.FindControl<TextBox>("SearchInputBoxSidebar")
                    if sb1 <> null && sb1.IsVisible then sb1
                    elif sb2 <> null && sb2.IsVisible then sb2
                    elif sb1 <> null then sb1
                    else sb2

                if searchBox <> null then
                    searchBox.Focus() |> ignore
                    searchBox.SelectAll()
        | _ -> ()

    member this.OnRescanClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartScanAsync()
        | _ -> ()

    member this.OnClearCacheClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ClearCacheAndRescan()
        | _ -> ()

    member this.OnAddCustomFolderClicked(sender: obj, e: RoutedEventArgs) =
        let options = FolderPickerOpenOptions()
        options.Title <- "Select Game Folder or Library"
        options.AllowMultiple <- false

        async {
            let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
            if folders <> null && folders.Count > 0 then
                let path = folders.[0].Path.LocalPath
                match this.DataContext with
                | :? MainViewModel as vm -> vm.AddCustomFolder(path)
                | _ -> ()
        }
        |> Async.StartImmediate

    member this.OnAddSingleGameFileClicked(sender: obj, e: RoutedEventArgs) =
        let options = FilePickerOpenOptions()
        options.Title <- "Select Game Executable (.exe)"
        options.AllowMultiple <- false
        let fileType = FilePickerFileType("Executable Files (*.exe)")
        fileType.Patterns <- [| "*.exe" |]
        options.FileTypeFilter <- [| fileType |]

        async {
            let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
            if files <> null && files.Count > 0 then
                let exePath = files.[0].Path.LocalPath
                match this.DataContext with
                | :? MainViewModel as vm -> vm.AddSingleGameExecutable(exePath)
                | _ -> ()
        }
        |> Async.StartImmediate

    /// Right-click flyout on a card: swap its artwork for a picture of the
    /// user's own.
    member this.OnChangeCoverClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card ->
                let options = FilePickerOpenOptions()
                options.Title <- "Choose a cover image"
                options.AllowMultiple <- false

                let fileType = FilePickerFileType("Images")
                fileType.Patterns <- [| "*.png"; "*.jpg"; "*.jpeg"; "*.bmp"; "*.webp"; "*.gif" |]
                options.FileTypeFilter <- [| fileType |]

                async {
                    let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                    if files <> null && files.Count > 0 then
                        vm.SetGameCover(card, files.[0].Path.LocalPath)
                }
                |> Async.StartImmediate
            | _ -> ()
        | _ -> ()

    /// Right-click flyout on a card: take the title out of the library.
    member this.OnRemoveGameClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? GameCardViewModel as card -> vm.RemoveGame(card)
            | _ -> ()
        | _ -> ()

    member this.OnToggleSectionClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) when not (isNull ctrl.Tag) ->
            vm.ToggleSection(string ctrl.Tag)
        | _ -> ()

    // =====================================================================
    // PAYLOAD ROWS, RESHADE SETUP, OPTISCALER, EXTRAS
    // =====================================================================
    member private this.PickFile(title: string, patterns: string[],typeLabel: string) =
        let options = FilePickerOpenOptions()
        options.Title <- title
        options.AllowMultiple <- false
        let fileType = FilePickerFileType(typeLabel)
        fileType.Patterns <- patterns
        options.FileTypeFilter <- [| fileType |]
        options

    member this.OnReplacePayloadRowClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? PayloadRowViewModel as row ->
                let extension = Path.GetExtension(row.Key)
                let options = this.PickFile("Select your own " + row.Key, [| "*" + extension |], row.Key)

                async {
                    let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                    if files <> null && files.Count > 0 then
                        vm.ReplacePayloadFile(row.Key, files.[0].Path.LocalPath)
                }
                |> Async.StartImmediate
            | _ -> ()
        | _ -> ()

    member this.OnRestorePayloadRowClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? PayloadRowViewModel as row -> vm.RestorePayloadFile(row.Key)
            | _ -> ()
        | _ -> ()

    member this.OnReplaceReShadeClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = this.PickFile("Select a ReShade setup executable", [| "*.exe" |], "ReShade setup (*.exe)")

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                if files <> null && files.Count > 0 then
                    vm.ReplaceReShadeSetup(files.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnRestoreReShadeClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RestoreReShadeSetup()
        | _ -> ()

    member this.OnReplaceOptiScalerClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FolderPickerOpenOptions()
            options.Title <- "Select the OptiScaler folder (must contain OptiScaler.dll)"
            options.AllowMultiple <- false

            async {
                let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
                if folders <> null && folders.Count > 0 then
                    vm.ReplaceOptiScaler(folders.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnReplaceOptiScalerNeuralClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FolderPickerOpenOptions()
            options.Title <- "Select the OptiScaler neural-upstream folder (must contain OptiScaler.dll)"
            options.AllowMultiple <- false

            async {
                let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
                if folders <> null && folders.Count > 0 then
                    vm.ReplaceOptiScalerNeural(folders.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnRestoreOptiScalerNeuralClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RestoreOptiScalerNeural()
        | _ -> ()

    /// Several at once, so a whole updated payload can go in one pick; only
    /// the names that already exist in the folder are used.
    member this.OnReplaceAmdPayloadClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FilePickerOpenOptions()
            options.Title <- "Select replacement files for the AMD payload"
            options.AllowMultiple <- true

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask

                if files <> null && files.Count > 0 then
                    vm.ReplaceAmdPayload([ for file in files -> file.Path.LocalPath ])
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnRestoreAmdPayloadClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RestoreAmdPayload()
        | _ -> ()

    member this.OnRestoreOptiScalerClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.RestoreOptiScaler()
        | _ -> ()

    member this.OnAddExtraFolderClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FolderPickerOpenOptions()
            options.Title <- "Select a folder to install with every mod"
            options.AllowMultiple <- false

            async {
                let! folders = this.StorageProvider.OpenFolderPickerAsync(options) |> Async.AwaitTask
                if folders <> null && folders.Count > 0 then
                    vm.AddExtras([ folders.[0].Path.LocalPath, true ])
            }
            |> Async.StartImmediate
        | _ -> ()

    /// Multiple files at once, so picking everything inside a folder puts those
    /// files beside the game rather than the folder itself.
    member this.OnAddExtraFilesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FilePickerOpenOptions()
            options.Title <- "Select files to install with every mod"
            options.AllowMultiple <- true

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask

                if files <> null && files.Count > 0 then
                    vm.AddExtras([ for file in files -> file.Path.LocalPath, false ])
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnToggleExtraModeClicked(sender: obj, e: RoutedEventArgs) =
        match sender with
        | :? Control as ctrl when not (isNull ctrl.Tag) ->
            match ctrl.DataContext with
            | :? ExtraRowViewModel as row -> row.ToggleMode(string ctrl.Tag)
            | _ -> ()
        | _ -> ()

    member this.OnRemoveExtraClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext, sender with
        | (:? MainViewModel as vm), (:? Control as ctrl) ->
            match ctrl.DataContext with
            | :? ExtraRowViewModel as row -> vm.RemoveExtra(row)
            | _ -> ()
        | _ -> ()


    // =====================================================================
    // MANAGE SHEET
    // =====================================================================
    member this.OnSetOptiScalerMode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallMode(ModInstaller.OptiScalerMode)
        | _ -> ()

    member this.OnSetDx12Mode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallMode(ModInstaller.Dx12Auto)
        | _ -> ()

    member this.OnSetDx11Mode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallMode(ModInstaller.Dx11)
        | _ -> ()

    member this.OnSetDx9Mode(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallMode(ModInstaller.Dx9)
        | _ -> ()

    member this.OnSetOptiDx12(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetOptiApi(ModInstaller.OptiDx12)
        | _ -> ()

    member this.OnSetOptiVulkan(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetOptiApi(ModInstaller.OptiVulkan)
        | _ -> ()

    member this.OnSetOptiNeural(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetOptiApi(ModInstaller.OptiNeural)
        | _ -> ()

    member this.OnSetNeuralAddonOn(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetNeuralAddon(true)
        | _ -> ()

    member this.OnSetNeuralAddonOff(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetNeuralAddon(false)
        | _ -> ()

    member this.OnSetOverlayOn(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.IsOverlayEnabled <- true
        | _ -> ()

    member this.OnSetOverlayOff(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.IsOverlayEnabled <- false
        | _ -> ()

    member this.OnSetBit64(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallArch(ModInstaller.Bit64)
        | _ -> ()

    member this.OnSetBit32(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetInstallArch(ModInstaller.Bit32)
        | _ -> ()

    member this.OnToggleTargetDetailsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ToggleTargetDetails()
        | _ -> ()

    member this.OnManageCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.CloseManage()
        | _ -> ()

    member this.OnManageBackdropPressed(sender: obj, e: PointerPressedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            vm.CloseManage()
            e.Handled <- true
        | _ -> ()

    member this.OnOpenGameFolderClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            try
                if not (String.IsNullOrWhiteSpace(vm.ManageExePath)) && File.Exists(vm.ManageExePath) then
                    // Opens Explorer with the executable already selected.
                    Process.Start(ProcessStartInfo("explorer.exe", sprintf "/select,\"%s\"" vm.ManageExePath))
                    |> ignore
                elif not (String.IsNullOrWhiteSpace(vm.ManageFolder)) && Directory.Exists(vm.ManageFolder) then
                    Process.Start(ProcessStartInfo(vm.ManageFolder, UseShellExecute = true)) |> ignore
            with _ ->
                ()
        | _ -> ()

    member this.OnChangeExecutableClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            let options = FilePickerOpenOptions()
            options.Title <- "Select the real game executable"
            options.AllowMultiple <- false
            let fileType = FilePickerFileType("Executable Files (*.exe)")
            fileType.Patterns <- [| "*.exe" |]
            options.FileTypeFilter <- [| fileType |]

            async {
                let! files = this.StorageProvider.OpenFilePickerAsync(options) |> Async.AwaitTask
                if files <> null && files.Count > 0 then
                    vm.SetManageExecutable(files.[0].Path.LocalPath)
            }
            |> Async.StartImmediate
        | _ -> ()

    member this.OnInstallDlssClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartInstall()
        | _ -> ()

    member this.OnSwitchModeClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartSwitch()
        | _ -> ()

    member this.OnUninstallDlssClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartUninstall()
        | _ -> ()

    /// Opens a link in the user's own browser. Failures are silent by design:
    /// a machine with no browser association must not throw at the user.
    member private this.OpenExternal(url: string) =
        try
            if not (String.IsNullOrWhiteSpace(url)) then
                Process.Start(ProcessStartInfo(url, UseShellExecute = true)) |> ignore
        with _ ->
            ()

    member this.OnDismissSupportPromptClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.DismissSupportPrompt()
        | _ -> ()

    /// Opening Ko-fi answers the prompt, so it closes behind the click.
    member this.OnSupportPromptDonateClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            this.OpenExternal(vm.DonateUrl)
            vm.DismissSupportPrompt()
        | _ -> ()

    member this.OnDonateClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.DonateUrl)
        | _ -> ()

    member this.OnSiteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.SiteUrl)
        | _ -> ()

    member this.OnOriginalSiteClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.OriginalSiteUrl)
        | _ -> ()

    /// The original developer's own Ko-fi - the other half of the 50-50 split.
    member this.OnOriginalDonateClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.OriginalDonateUrl)
        | _ -> ()

    member this.OnSupportPromptOriginalDonateClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            this.OpenExternal(vm.OriginalDonateUrl)
            vm.DismissSupportPrompt()
        | _ -> ()

    // =====================================================================
    // BATCH SHEET
    // =====================================================================
    member this.OnBatchOpenClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.OpenBatch()
        | _ -> ()

    member this.OnBatchCloseClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.CloseBatch()
        | _ -> ()

    /// Clicking the dimmed area behind the sheet closes it, but not while a
    /// run is in progress - the installer is partway through a game folder.
    member this.OnBatchBackdropPressed(sender: obj, e: PointerPressedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.CloseBatch()
        | _ -> ()

    member this.OnBatchSelectAllClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SelectAllBatch()
        | _ -> ()

    member this.OnBatchSelectNoneClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.ClearBatchSelection()
        | _ -> ()

    /// The checkbox writes Selected itself; this only nudges the counter and
    /// the run button, which live on the parent view model.
    member this.OnBatchRowToggled(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.NotifyBatchSelectionChanged()
        | _ -> ()

    member this.OnBatchInstallClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartBatchInstall()
        | _ -> ()

    member this.OnBatchUninstallClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.StartBatchUninstall()
        | _ -> ()

    member this.OnTutorialsClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.TutorialsUrl)
        | _ -> ()

    /// The original's own download page. Required by the terms this build was
    /// given permission under, and it goes to NODIX TECH's site rather than
    /// anywhere this build controls.
    member this.OnOriginalDownloadClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.OriginalDownloadUrl)
        | _ -> ()

    member this.OnGuidesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if not (String.IsNullOrWhiteSpace(vm.GuidesUrl)) then this.OpenExternal(vm.GuidesUrl)
        | _ -> ()

    /// The header pill only exists while an update is waiting, so it always
    /// goes straight to the download page.
    member this.OnUpdateBadgeClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> this.OpenExternal(vm.DownloadPageUrl)
        | _ -> ()

    /// First press checks GitHub; once an update is known it opens the
    /// official download page instead.
    member this.OnCheckUpdatesClicked(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm ->
            if vm.HasUpdateAvailable then
                try
                    Process.Start(ProcessStartInfo(vm.DownloadPageUrl, UseShellExecute = true)) |> ignore
                with _ ->
                    ()
            else
                vm.CheckForUpdates()
        | _ -> ()

    member this.OnSetTopBarLayout(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetLayoutMode(false)
        | _ -> ()

    member this.OnSetSidebarLayout(sender: obj, e: RoutedEventArgs) =
        match this.DataContext with
        | :? MainViewModel as vm -> vm.SetLayoutMode(true)
        | _ -> ()
