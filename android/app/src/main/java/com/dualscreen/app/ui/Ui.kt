package com.dualscreen.app.ui

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.Switch
import android.widget.TextView
import com.dualscreen.app.R

/**
 * Small view kit that reproduces DualScreen for Windows (UI/Theme.xaml) on Android: page titles, section
 * subtitles, 10 dp cards with "label + caption on the left, control on the right" rows, accent buttons,
 * Windows-style option cards and toggle switches.
 */
object Ui {
    fun dp(c: Context, v: Float) = (v * c.resources.displayMetrics.density).toInt()

    fun shape(c: Context, color: Int, radiusDp: Float, stroke: Int? = null, strokeDp: Float = 1f) = GradientDrawable().apply {
        setColor(color); cornerRadius = radiusDp * c.resources.displayMetrics.density
        if (stroke != null) setStroke(dp(c, strokeDp), stroke)
    }

    fun title(c: Context, text: String) = TextView(c).apply {
        this.text = text; textSize = 28f; setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL))
        setTextColor(c.getColor(R.color.text)); setPadding(0, 0, 0, dp(c, 18f))
    }

    fun subtitle(c: Context, text: String) = TextView(c).apply {
        this.text = text; textSize = 15f; setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL))
        setTextColor(c.getColor(R.color.text)); setPadding(dp(c, 2f), dp(c, 18f), 0, dp(c, 8f))
    }

    fun caption(c: Context, text: String, color: Int = R.color.text2) = TextView(c).apply {
        this.text = text; textSize = 13f; setTextColor(c.getColor(color))
    }

    fun body(c: Context, text: String, size: Float = 15f) = TextView(c).apply {
        this.text = text; textSize = size; setTextColor(c.getColor(R.color.text))
    }

    /** Card container (vertical). */
    fun card(c: Context): LinearLayout = LinearLayout(c).apply {
        orientation = LinearLayout.VERTICAL
        setBackgroundResource(R.drawable.card)
        setPadding(dp(c, 16f), dp(c, 14f), dp(c, 16f), dp(c, 14f))
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(c, 6f) }
    }

    /** Windows "DockPanel" row: title + caption on the left, [right] control on the right. */
    fun row(c: Context, title: String, caption: String?, right: View?, onClick: (() -> Unit)? = null): LinearLayout = LinearLayout(c).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        setBackgroundResource(R.drawable.card)
        setPadding(dp(c, 16f), dp(c, 14f), dp(c, 14f), dp(c, 14f))
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(c, 6f) }
        addView(LinearLayout(c).apply {
            orientation = LinearLayout.VERTICAL
            addView(body(c, title))
            if (!caption.isNullOrEmpty()) addView(caption(c, caption).apply { setPadding(0, dp(c, 2f), dp(c, 8f), 0) })
        }, LinearLayout.LayoutParams(0, -2, 1f))
        if (right != null) addView(right)
        if (onClick != null) { isClickable = true; setOnClickListener { onClick() } }
    }

    fun toggle(c: Context, checked: Boolean, onChange: (Boolean) -> Unit) = Switch(c).apply {
        isChecked = checked
        thumbTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
            intArrayOf(c.getColor(R.color.on_accent), c.getColor(R.color.text3)))
        trackTintList = ColorStateList(arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
            intArrayOf(c.getColor(R.color.accent), c.getColor(R.color.surface_hover)))
        setOnCheckedChangeListener { _, v -> onChange(v) }
    }

    fun toggleRow(c: Context, title: String, caption: String?, checked: Boolean, onChange: (Boolean) -> Unit): LinearLayout {
        val sw = toggle(c, checked, onChange)
        return row(c, title, caption, sw) { sw.toggle() }
    }

    fun button(c: Context, text: String, accent: Boolean, onClick: () -> Unit) = TextView(c).apply {
        this.text = text; textSize = 15f; gravity = Gravity.CENTER
        setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL))
        setTextColor(c.getColor(if (accent) R.color.on_accent else R.color.text))
        setBackgroundResource(if (accent) R.drawable.btn_accent else R.drawable.btn_tonal)
        setPadding(dp(c, 18f), 0, dp(c, 18f), 0)
        minHeight = dp(c, 44f)
        if (accent) elevation = dp(c, 3f).toFloat()
        setOnClickListener { onClick() }
    }

    /** Value + chevron, like a Windows ComboBox; opens a single-choice list. */
    fun choiceRow(a: Activity, title: String, caption: String?, options: List<Pair<String, String>>, selected: String,
                  enabled: Boolean = true, onPick: (String) -> Unit): LinearLayout {
        val label = options.firstOrNull { it.first == selected }?.second ?: options.firstOrNull()?.second ?: "—"
        val value = TextView(a).apply {
            text = "$label  ⌄"; textSize = 14f; setTextColor(a.getColor(if (enabled) R.color.text else R.color.text3))
            background = shape(a, a.getColor(R.color.surface_high), 7f, a.getColor(R.color.stroke))
            setPadding(dp(a, 12f), dp(a, 8f), dp(a, 12f), dp(a, 8f))
            maxWidth = dp(a, 170f); maxLines = 1
        }
        return row(a, title, caption, value) {
            if (!enabled) return@row
            val idx = options.indexOfFirst { it.first == selected }.coerceAtLeast(0)
            AlertDialog.Builder(a, android.R.style.Theme_Material_Dialog_Alert)
                .setTitle(title)
                .setSingleChoiceItems(options.map { it.second }.toTypedArray(), idx) { d, which ->
                    d.dismiss()
                    if (options[which].first != selected) onPick(options[which].first)
                }
                .setNegativeButton("Cancelar", null)
                .show()
        }.also { if (!enabled) it.alpha = 0.5f }
    }

    /** Windows "OptionCard": icon, title, caption; selected = accent border. */
    fun optionCard(c: Context, icon: Int, title: String, caption: String, selected: Boolean, onClick: () -> Unit) = LinearLayout(c).apply {
        orientation = LinearLayout.VERTICAL
        setBackgroundResource(if (selected) R.drawable.card_selected else R.drawable.card)
        setPadding(dp(c, 14f), dp(c, 14f), dp(c, 14f), dp(c, 14f))
        addView(ImageView(c).apply { setImageResource(icon); imageTintList = ColorStateList.valueOf(c.getColor(R.color.text)) },
            LinearLayout.LayoutParams(dp(c, 24f), dp(c, 24f)).apply { bottomMargin = dp(c, 10f) })
        addView(body(c, title).apply { setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL)) })
        addView(caption(c, caption).apply { setPadding(0, dp(c, 4f), 0, 0) })
        setOnClickListener { onClick() }
    }

    fun dot(c: Context, color: Int, sizeDp: Float = 10f) = View(c).apply {
        background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(c.getColor(color)) }
        layoutParams = LinearLayout.LayoutParams(dp(c, sizeDp), dp(c, sizeDp))
    }

    /** Stats tile (label + big value), as in the Windows "Transmisión" grid. */
    fun statTile(c: Context, label: String, value: String) = LinearLayout(c).apply {
        orientation = LinearLayout.VERTICAL
        setBackgroundResource(R.drawable.card)
        setPadding(dp(c, 14f), dp(c, 12f), dp(c, 14f), dp(c, 12f))
        addView(caption(c, label))
        addView(TextView(c).apply {
            text = value; textSize = 18f; setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL))
            setTextColor(c.getColor(R.color.text)); maxLines = 1; setPadding(0, dp(c, 4f), 0, 0)
        })
    }
}
