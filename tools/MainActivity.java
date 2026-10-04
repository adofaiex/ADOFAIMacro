// tools/MainActivity.java
//
// 匀速谱生成器（本项目作者 HitMargin / QQ 2228293026 的作品）。
// 把任意 .adofai 转成 _steady.adofai：逐块砖算出真实旋转角度 rlangle_，
// 再按 bpm = rlangle_/180 插入 SetSpeed，使每块砖按匀速 BPM 旋转。
//
// 它同时是 TechniqueSimulator 角度感知片长的算法依据：反运算可得
// 「该砖从进场到压线实际占 dt * 建议bpm / 60 拍」。
//
// 原工程（Android Studio 工程）不在本仓库，此处仅保留主类源码备查。
package hitmargin.adofai.uniformspeed;

import android.Manifest;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.os.Environment;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.TextView;
import android.widget.Toast;

import androidx.annotation.NonNull;
import androidx.appcompat.app.AppCompatActivity;

import androidx.core.app.ActivityCompat;
import androidx.core.content.ContextCompat;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;
import java.io.FileWriter;
import java.io.IOException;
import java.util.HashMap;
import java.util.Map;

public class MainActivity extends AppCompatActivity {
    private static final int REQUEST_CODE_PERMISSIONS = 1001;
    private static final String[] REQUIRED_PERMISSIONS = {
            Manifest.permission.READ_EXTERNAL_STORAGE,
            Manifest.permission.WRITE_EXTERNAL_STORAGE
    };
    
    private EditText editTextPath;
    private Button buttonProcess;
    private TextView textViewResult;

    private Map<Character, Double> pathAngleMap = new HashMap<>();
    private double[] angleData = new double[100000];
    private double[] pause = new double[100000];
    private double[] rlangle_ = new double[100000];
    private int[] multiPlanets = new int[100000];
    private int[] rotation = new int[100000];
    private double[] bpm = new double[100000];
    private double[] rlbpm = new double[100000];

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        Toast.makeText(getApplication(), "构建日期 : 2025.01.11\n作者：HitMargin | QQ：2228293026", Toast.LENGTH_SHORT).show();
        if (!allPermissionsGranted()) {
            ActivityCompat.requestPermissions(this, REQUIRED_PERMISSIONS, REQUEST_CODE_PERMISSIONS);
        } else {
            setupUI();
        }
    }

    private boolean allPermissionsGranted() {
        for (String permission : REQUIRED_PERMISSIONS) {
            if (ContextCompat.checkSelfPermission(this, permission) != PackageManager.PERMISSION_GRANTED) {
                return false;
            }
        }
        return true;
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, @NonNull String[] permissions, @NonNull int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == REQUEST_CODE_PERMISSIONS) {
            if (allPermissionsGranted()) {
                setupUI();
            } else {
                Toast.makeText(this, "权限请求被拒绝", Toast.LENGTH_SHORT).show();
                finish(); // 如果权限被拒绝，结束应用
            }
        }
    }

    private void setupUI() {
        editTextPath = findViewById(R.id.editTextPath);
        buttonProcess = findViewById(R.id.buttonProcess);
        textViewResult = findViewById(R.id.textViewResult);

        // 初始化path-angle对照表
        initializePathAngleMap();

        buttonProcess.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                processFile();
            }
        });
    }

    private void initializePathAngleMap() {
        pathAngleMap.put('p', 15.0);
        pathAngleMap.put('J', 30.0);
        pathAngleMap.put('E', 45.0);
        pathAngleMap.put('T', 60.0);
        pathAngleMap.put('t', 60.0);
        pathAngleMap.put('o', 75.0);
        pathAngleMap.put('U', 90.0);
        pathAngleMap.put('q', 105.0);
        pathAngleMap.put('G', 120.0);
        pathAngleMap.put('h', 120.0);
        pathAngleMap.put('Q', 135.0);
        pathAngleMap.put('H', 150.0);
        pathAngleMap.put('W', 165.0);
        pathAngleMap.put('L', 180.0);
        pathAngleMap.put('x', 195.0);
        pathAngleMap.put('N', 210.0);
        pathAngleMap.put('Z', 225.0);
        pathAngleMap.put('F', 240.0);
        pathAngleMap.put('j', 240.0);
        pathAngleMap.put('V', 255.0);
        pathAngleMap.put('D', 270.0);
        pathAngleMap.put('Y', 285.0);
        pathAngleMap.put('B', 300.0);
        pathAngleMap.put('y', 300.0);
        pathAngleMap.put('C', 315.0);
        pathAngleMap.put('M', 330.0);
        pathAngleMap.put('A', 345.0);
        pathAngleMap.put('!', 999.0);
    }

    private void processFile() {
        String path = editTextPath.getText().toString();
        File file = new File(path);

        if (!file.exists()) {
            Toast.makeText(this, "文件不存在", Toast.LENGTH_SHORT).show();
            return;
        }

        try {
            BufferedReader reader = new BufferedReader(new FileReader(file));
            String line;
            StringBuilder content = new StringBuilder();

            while ((line = reader.readLine()) != null) {
                content.append(line).append("\n");
            }

            reader.close();

            // 处理文件内容
            String processedContent = processContent(content.toString());

            // 保存处理后的内容
            File newFile = new File(file.getParent(), file.getName().replace(".adofai", "_steady.adofai"));
            FileWriter writer = new FileWriter(newFile);
            writer.write(processedContent);
            writer.close();

            Toast.makeText(this, "处理完成，新文件已保存到：" + newFile, Toast.LENGTH_LONG).show();
        } catch (IOException e) {
            e.printStackTrace();
            Toast.makeText(this, "处理文件时出错：" + e.getMessage(), Toast.LENGTH_LONG).show();
        }
    }

    private String processContent(String content) {
        String[] lines = content.split("\n");
        int cnt = 0;
        rotation[0] = -1;

        for (String line : lines) {
            if (line.contains("angleData")) {
                cnt = angle(line);
                continue;
            }

            if (line.contains("pathData")) {
                cnt = path_(line);
                continue;
            }

            String eventType = get(line, "eventType");
            if (!eventType.isEmpty()) {
                if (eventType.equals("\"Twirl\"")) {
                    rotation[cnt] = -1 * rotation[cnt - 1];
                    continue;
                }
                if (eventType.equals("\"Pause\"")) {
                    int floor = Integer.parseInt(get(line, "floor"));
                    pause[floor] = Double.parseDouble(get(line, "duration"));
                    continue;
                }
                if (eventType.equals("\"MultiPlanet\"")) {
                    int floor = Integer.parseInt(get(line, "floor"));
                    if (get(line, "planets").equals("\"ThreePlanets\"")) {
                        multiPlanets[floor] = 1;
                    } else {
                        multiPlanets[floor] = -1;
                    }
                    continue;
                }
            }
        }

        // 处理数据
        for (int now_floor = 1; now_floor < cnt; now_floor++) {
            if (rotation[now_floor] == 0) {
                rotation[now_floor] = rotation[now_floor - 1];
            }
            if (multiPlanets[now_floor] == 0) {
                multiPlanets[now_floor] = multiPlanets[now_floor - 1];
            }

            double start_angle = angleData[now_floor - 1] - 180;
            while (start_angle < 0) {
                start_angle += 360;
            }
            double end_angle = angleData[now_floor];

            if (end_angle == 999) {
                angleData[now_floor] = angleData[now_floor - 1] - 180;
                while (angleData[now_floor] < 0) {
                    angleData[now_floor] += 360;
                }
                rlangle_[now_floor] = -999;
                continue;
            }

            if (rotation[now_floor] == -1) {
                while (start_angle <= end_angle) {
                    start_angle += 360;
                }
            } else {
                while (start_angle >= end_angle) {
                    start_angle -= 360;
                }
            }

            double real_angle = (end_angle - start_angle) * rotation[now_floor];
            if (multiPlanets[now_floor] == 1) {
                real_angle -= 60;
            }

            while (real_angle < 0.01) {
                real_angle += 360;
            }
            rlangle_[now_floor] = computeAngle(real_angle, pause[now_floor]);
        }

        // 生成新的文件内容
        StringBuilder newContent = new StringBuilder();
        int now_floor = 1;
        int act = -1;

        for (String line : lines) {
            String eventType = get(line, "eventType");
            if (eventType.equals("\"SetSpeed\"")) {
                continue;
            }
            int pl = line.indexOf("\"actions\":");
            if (pl != -1) {
                act = 1;
            }
            if (act == 1) {
                pl = line.indexOf("],");
                if (pl != -1 && eventType.isEmpty()) {
                    act = 0;
                    while (now_floor < cnt - 2) {
                        if (rlangle_[now_floor] != -999) {
                            bpm[now_floor] = rlangle_[now_floor] / 180;
                            rlbpm[now_floor] = bpm[now_floor] / bpm[now_floor - 1];
                            if (rlbpm[now_floor] != 1) {
                                newContent.append("{ \"floor\": ").append(now_floor)
                                        .append(", \"eventType\": \"SetSpeed\", \"speedType\": \"Multiplier\", \"beatsPerMinute\": 100, \"bpmMultiplier\": ")
                                        .append(rlbpm[now_floor]).append(", \"angleOffset\": 0 },\n");
                            }
                        } else {                            rlangle_[now_floor] = rlangle_[now_floor - 1];
                            bpm[now_floor] = rlangle_[now_floor] / 180;
                            rlbpm[now_floor] = bpm[now_floor] / bpm[now_floor - 1];
                        }
                        now_floor++;
                    }
                    if (rlangle_[now_floor] != -999) {
                        bpm[now_floor] = rlangle_[now_floor] / 180;
                        rlbpm[now_floor] = bpm[now_floor] / bpm[now_floor - 1];
                        if (rlbpm[now_floor] != 1) {
                            newContent.append("{ \"floor\": ").append(now_floor)
                                    .append(", \"eventType\": \"SetSpeed\", \"speedType\": \"Multiplier\", \"beatsPerMinute\": 100, \"bpmMultiplier\": ")
                                    .append(rlbpm[now_floor]).append(", \"angleOffset\": 0 }\n");
                        }
                    }
                }
            }

            if (!eventType.isEmpty() && act == 1) {
                int floor = Integer.parseInt(get(line, "floor"));
                while (now_floor <= floor) {
                    if (rlangle_[now_floor] != -999) {
                        bpm[now_floor] = rlangle_[now_floor] / 180;
                        rlbpm[now_floor] = bpm[now_floor] / bpm[now_floor - 1];
                        if (rlbpm[now_floor] != 1) {
                            newContent.append("{ \"floor\": ").append(now_floor)
                                    .append(", \"eventType\": \"SetSpeed\", \"speedType\": \"Multiplier\", \"beatsPerMinute\": 100, \"bpmMultiplier\": ")
                                    .append(rlbpm[now_floor]).append(", \"angleOffset\": 0 },\n");
                        }
                    } else {
                        rlangle_[now_floor] = rlangle_[now_floor - 1];
                        bpm[now_floor] = rlangle_[now_floor] / 180;
                        rlbpm[now_floor] = bpm[now_floor] / bpm[now_floor - 1];
                    }
                    now_floor++;
                }
            }
            newContent.append(line).append("\n");
        }

        return newContent.toString();
    }

    private int angle(String line) {
        int n = 0;
        int start = line.indexOf("[");
        int end = line.indexOf("]");
        line = line.substring(start + 1, end);
        line = line + ",";
        while (!line.isEmpty()) {
            start = line.indexOf(",");
            angleData[n] = Double.parseDouble(line.substring(0, start));
            while (angleData[n] <= 0) {
                angleData[n] += 360;
            }
            while (angleData[n] >= 360 && angleData[n] != 999) {
                angleData[n] -= 360;
            }
            n++;
            line = line.substring(start + 1);
        }
        return n;
    }

    private int path_(String pline) {
        pline = pline.substring(11, pline.length() - 11);
        int n = 0;

        int start = pline.indexOf("\"");
        pline = pline.substring(start + 1);
        start = pline.indexOf("\"");
        pline = pline.substring(0, start);

        while (!pline.isEmpty()) {
            char chr_ = pline.charAt(0);
            if (pathAngleMap.containsKey(chr_)) {
                angleData[n] = pathAngleMap.get(chr_);
            } else {
                angleData[n] = 0;
            }
            n++;
            pline = pline.substring(1);
        }
        return n;
    }

    private double computeAngle(double angle, double pau) {
        return angle + pau * 180;
    }

    private String get(String line, String tar) {
        while (line.endsWith(",") || line.endsWith(" ") || line.endsWith("}")) {
            if (line.length() == 1) {
                return "";
            }
            line = line.substring(0, line.length() - 1);
        }
        line = line + ",";

        int start = line.indexOf(tar);
        if (start == -1) {
            return "";
        }
        line = line.substring(start);

        start = line.indexOf(":");
        line = line.substring(start + 1);

        start = line.indexOf(" ");
        while (start == 0) {
            line = line.substring(start + 1);
            start = line.indexOf(" ");
        }

        int end = line.indexOf(",");
        line = line.substring(0, end);

        return line;
    }
}
