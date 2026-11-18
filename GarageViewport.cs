using UnityEngine;

internal sealed class GarageViewport
{
    private readonly UIPanel list;
    private readonly Camera camera;
    private readonly Transform listTransform;
    private readonly Transform listAnchor;
    private readonly UIWidget footer;
    private readonly UIWidget swipeBackground;

    public GarageViewport(UIPanel list, UICenterOnChildWithPivot snap, Camera camera,
        GameObject moreCars, GameObject swipeHint)
    {
        this.list = list;
        this.camera = camera;
        listTransform = list.transform;
        listAnchor = listTransform.parent;

        var footerSprite = moreCars.transform.Find("MoreCarsBG").GetComponent<UISprite>();
        footerSprite.spriteName = "UiFeWhiteSliced";
        footerSprite.color = new Color(0.08f, 0.08f, 0.08f, 1f);
        footer = footerSprite;

        if (swipeHint != null)
        {
            swipeBackground = swipeHint.transform.Find("SwipeLabelBG").GetComponent<UISlicedSprite>();
            var swipeLabel = swipeHint.transform.Find("SwipeLabel").GetComponent<UIAnchorPixelOffset>();
            float footerHeight = footer.GetComponent<UIStretchPixelAdjust>().pixelAdjust.y;
            float hintHeight = swipeBackground.GetComponent<UIStretchPixelAdjust>().pixelAdjust.y;
            swipeLabel.pixelOffset.y = footerHeight + hintHeight * 0.5f;
            swipeBackground.alpha = 1f;
        }

        // The original stretch component centers its rectangle on the top anchor.
        // Align the snap point with the top of the visible rectangle instead.
        list.GetComponent<UIStretchPanel>().enabled = false;
        snap.side = UICenterOnChildWithPivot.Side.Top;
        list.clipping = UIDrawCall.Clipping.HardClip;
    }

    public void UpdateClip()
    {
        float top = listTransform.InverseTransformPoint(listAnchor.position).y;
        float bottom = TopOf(footer);
        if (swipeBackground != null && swipeBackground.gameObject.activeInHierarchy)
            bottom = Mathf.Max(bottom, TopOf(swipeBackground));

        var left = listTransform.InverseTransformPoint(camera.ViewportToWorldPoint(Vector3.zero));
        var right = listTransform.InverseTransformPoint(camera.ViewportToWorldPoint(Vector3.right));
        float width = right.x - left.x;
        if (top > bottom && width > 0f)
        {
            var range = new Vector4(0f, (top + bottom) * 0.5f, width, top - bottom);
            if (list.clipRange != range)
                list.clipRange = range;
        }
    }

    private float TopOf(UIWidget widget)
    {
        var transform = widget.transform;
        float height = widget.relativeSize.y;
        float pivotY = widget.pivotOffset.y * height;
        var first = listTransform.InverseTransformPoint(transform.TransformPoint(new Vector3(0f, pivotY, 0f)));
        var second = listTransform.InverseTransformPoint(transform.TransformPoint(new Vector3(0f, pivotY - height, 0f)));
        return Mathf.Max(first.y, second.y);
    }

    public static void PreserveFrontEndPage(UIGrid grid, float previousWidth)
    {
        if (previousWidth <= 0f || grid.cellWidth <= 0f || Mathf.Approximately(previousWidth, grid.cellWidth))
            return;

        var parent = grid.transform.parent;
        var frontEnd = parent == null ? null : parent.GetComponent<UIFrontEndRoot>();
        if (frontEnd == null || frontEnd.m_grid != grid || frontEnd.m_draggablePanel == null)
            return;

        // The grid lays out its children in the new pixels-per-page. Keep the same
        // page position and any in-progress snap target on that scale.
        float scale = grid.cellWidth / previousWidth;
        var drag = frontEnd.m_draggablePanel;
        var spring = drag.GetComponent<SpringPanel>();
        if (spring != null && spring.enabled)
            spring.target.x *= scale;

        var momentum = drag.currentMomentum;
        if (momentum.x != 0f)
        {
            momentum.x *= scale;
            drag.currentMomentum = momentum;
        }

        float offset = drag.transform.localPosition.x;
        if (offset != 0f)
            drag.MoveRelative(new Vector3(offset * scale - offset, 0f, 0f));
    }

    public static GameObject PreparePreview(GameObject preview)
    {
        if (preview == null) return preview;

        // The large multiply plane can overlap the car in the row above.
        var curtain = preview.transform.Find("BlackoutPlane");
        if (curtain != null)
        {
            var renderer = curtain.GetComponent<Renderer>();
            if (renderer != null)
                renderer.enabled = false;
        }
        return preview;
    }
}
